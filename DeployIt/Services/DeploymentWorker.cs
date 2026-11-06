using System.Text.Json;
using System.Text.RegularExpressions;
using DeployIt.Data;
using DeployIt.DTOs;
using DeployIt.Models;
using Microsoft.EntityFrameworkCore;

namespace DeployIt.Services;

public sealed class DeploymentWorker(IDbContextFactory<DeployItDbContext> factory,
    SshDeploymentService ssh, SecretProtector secrets, ILogger<DeploymentWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private const int MaxLogLength = 1_000_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallelism = configuration.GetValue("Deployments:MaxParallelDeployments", 4);
        if (parallelism is < 1 or > 32)
            throw new InvalidOperationException("Deployments:MaxParallelDeployments must be between 1 and 32.");
        await using (var db = await factory.CreateDbContextAsync(stoppingToken))
        {
            await db.Deployments.Where(d => d.Status == DeploymentStatus.Running && !d.Durable).ExecuteUpdateAsync(update => update
                .SetProperty(d => d.Status, DeploymentStatus.Interrupted)
                .SetProperty(d => d.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(d => d.ProtectedSnapshot, "")
                .SetProperty(d => d.ErrorMessage, "Панель была перезапущена. Результат удалённой команды неизвестен; проверьте сервер перед повторным запуском.")
                .SetProperty(d => d.Log, d => d.Log + "\nПанель была перезапущена. Проверьте сервер.\n"), stoppingToken);
        }
        var active = new Dictionary<Guid, Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Observe every task before releasing its slot. One failed run must not stop others.
                foreach (var completed in active.Where(pair => pair.Value.IsCompleted).ToArray())
                {
                    try { await completed.Value; }
                    catch (Exception e) { logger.LogError("Deployment task error: {ExceptionType}", e.GetType().Name); }
                    active.Remove(completed.Key);
                }
                try
                {
                    var slots = parallelism - active.Count;
                    if (slots > 0)
                    {
                        await using var db = await factory.CreateDbContextAsync(stoppingToken);
                        var activeIds = active.Keys.ToArray();
                        var candidates = await db.Deployments.AsNoTracking()
                            .Where(d => !activeIds.Contains(d.Id) && (d.Status == DeploymentStatus.Queued
                                || (d.Status == DeploymentStatus.Running && d.Durable)))
                            .OrderBy(d => d.Status == DeploymentStatus.Running ? 0 : 1)
                            .ThenBy(d => d.CreatedAt).Take(slots).ToListAsync(stoppingToken);
                        foreach (var next in candidates)
                        {
                            var resume = next.Status == DeploymentStatus.Running;
                            if (!resume)
                            {
                                // Keep the conditional claim: a simultaneous queue cancellation wins safely.
                                var claimed = await db.Deployments.Where(d => d.Id == next.Id && d.Status == DeploymentStatus.Queued)
                                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DeploymentStatus.Running)
                                        .SetProperty(d => d.Durable, true)
                                        .SetProperty(d => d.StartedAt, DateTimeOffset.UtcNow), stoppingToken);
                                if (claimed != 1) continue;
                                next.Durable = true;
                            }
                            // RunAsync already uses asynchronous SSH and database I/O; don't await the whole run here.
                            active.Add(next.Id, RunAsync(next, stoppingToken, resume));
                        }
                    }
                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    // Credentials, commands and output never enter application logs.
                    logger.LogError("Deployment queue error: {ExceptionType}", e.GetType().Name);
                    try { await Task.Delay(2000, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }
        }
        finally
        {
            // Finish each run's checkpoint and preserve its snapshot before the host closes the database.
            try { await Task.WhenAll(active.Values); }
            catch (Exception e) { logger.LogError("Deployment shutdown error: {ExceptionType}", e.GetType().Name); }
        }
    }

    private async Task RunAsync(Deployment deployment, CancellationToken stoppingToken, bool resume = false)
    {
        using var logLock = new SemaphoreSlim(1, 1);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sensitive = new List<string>();
        var stage = resume && deployment.Stage != "" ? deployment.Stage : "ssh";
        var tail = resume ? deployment.Log[^Math.Min(16000, deployment.Log.Length)..] : "";
        string Redact(string text)
        {
            foreach (var value in sensitive.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
                text = text.Replace(value, "[скрыто]", StringComparison.Ordinal);
            return text;
        }
        async Task Append(string line)
        {
            line = Redact(line);
            await logLock.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var run = await db.Deployments.FindAsync(deployment.Id);
                if (run is null) return;
                var marker = line.StartsWith("[stderr] ") ? line[9..] : line;
                if (marker.StartsWith("DEPLOYIT_STAGE="))
                {
                    stage = marker[15..].Trim();
                    run.Stage = DeploymentDiagnostics.StageName(stage);
                    line = "Этап: " + run.Stage + ".";
                }
                else if (marker.StartsWith("DEPLOYIT_FAILURE="))
                {
                    var failure = marker[17..].Split(':');
                    stage = failure[0];
                    run.Stage = DeploymentDiagnostics.StageName(stage);
                    line = $"[ошибка] Этап «{run.Stage}», код команды {failure.ElementAtOrDefault(1) ?? "не получен"}.";
                }
                var commit = Regex.Match(line, "^DEPLOYIT_COMMIT=([a-f0-9]{40,64})$");
                if (commit.Success && run.CommitSha is null) run.CommitSha = commit.Groups[1].Value;
                tail += line + "\n";
                if (tail.Length > 16000) tail = tail[^16000..];
                var combined = run.Log + line + "\n";
                if (combined.Length > MaxLogLength)
                {
                    // Preserve the beginning AND the newest output, including the actual failure.
                    run.Log = combined[..200000] + "\n[Середина журнала сокращена; последние строки сохранены.]\n" + combined[^750000..];
                    run.LogTruncated = true;
                }
                else run.Log = combined;
                await db.SaveChangesAsync();
            }
            finally { logLock.Release(); }
        }
        var status = DeploymentStatus.Failed;
        int? exit = null;
        string? error = null;
        var requested = false;
        var stopConfirmed = false;
        string? stopError = null;
        DeploymentSnapshot? snapshot = null;
        Task? monitor = null;
        try
        {
            snapshot = JsonSerializer.Deserialize<DeploymentSnapshot>(secrets.Unprotect(deployment.ProtectedSnapshot))
                ?? throw new InvalidOperationException("Invalid deployment snapshot");
            sensitive.Add(secrets.Unprotect(snapshot.Connection.ProtectedToken));
            sensitive.AddRange(secrets.Unprotect(snapshot.Project.ProtectedPrivateKey).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 10));
            sensitive.Add(secrets.Unprotect(snapshot.Project.ProtectedPassphrase));
            var password = secrets.Unprotect(snapshot.Project.ProtectedPassword);
            sensitive.Add(password);
            sensitive.AddRange(password.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            monitor = WatchCancellation(snapshot.Project);
            async Task Checkpoint(long offset) {
                await using var checkpointDb = await factory.CreateDbContextAsync();
                await checkpointDb.Deployments.Where(d => d.Id == deployment.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.RemoteLogOffset, offset));
            }
            if (resume) await Append("Панель перезапущена. Проверяем прежний удалённый запуск без повторного выполнения команды.");
            exit = resume ? await ssh.ResumeAsync(snapshot, deployment.Id, Append, execution.Token, deployment.RemoteLogOffset, Checkpoint) : deployment.Operation switch
            {
                DeploymentOperation.Restart => await ssh.RestartAsync(snapshot, Append, execution.Token, deployment.Id, deployment.RemoteLogOffset, Checkpoint),
                DeploymentOperation.Stop => await ssh.StopAsync(snapshot, deployment.Id, Append, execution.Token, deployment.RemoteLogOffset, Checkpoint),
                DeploymentOperation.Deploy => await ssh.DeployAsync(snapshot, deployment.Id, Append, execution.Token, deployment.RemoteLogOffset, Checkpoint),
                _ => throw new DomainException("Неизвестное действие проекта. Обновите панель и повторите запуск.")
            };
            status = exit == 0 ? DeploymentStatus.Succeeded : DeploymentStatus.Failed;
            if (exit != 0) error = DeploymentDiagnostics.Failure(exit, stage, tail, snapshot.Project.TimeoutMinutes);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            status = DeploymentStatus.Interrupted;
            error = "Выполнение прервано. Результат удалённой команды неизвестен; проверьте сервер.";
        }
        catch (RemoteOutcomeUnknownException e)
        {
            status = DeploymentStatus.Interrupted;
            error = e.Message;
        }
        catch (Exception e)
        {
            error = DeploymentDiagnostics.ExceptionFailure(e, stage, snapshot?.Project.TimeoutMinutes ?? 20);
        }
        finally
        {
            watcher.Cancel();
            if (monitor is not null)
            {
                try { await monitor; }
                catch (OperationCanceledException) { }
                catch (Exception e) { stopError = $"Проверка запроса остановки не завершена ({e.GetType().Name})."; }
            }
        }
        if (stoppingToken.IsCancellationRequested && deployment.Durable && !requested)
        {
            await Append("Панель остановлена. Удалённый запуск продолжает работать автономно; проверка результата возобновится после запуска панели.");
            return;
        }
        if (requested && status != DeploymentStatus.Succeeded)
        {
            status = stopConfirmed ? DeploymentStatus.Canceled : DeploymentStatus.Interrupted;
            error = stopConfirmed ? "Запуск остановлен пользователем. Удалённая команда завершена."
                : "Остановку на сервере подтвердить не удалось. " + stopError + " Проверьте процессы в SSH-терминале перед новым запуском.";
        }
        else if (stoppingToken.IsCancellationRequested && status != DeploymentStatus.Succeeded)
        {
            status = DeploymentStatus.Interrupted;
            error = "Панель остановлена. Удалённая команда может продолжаться до таймаута; проверьте сервер.";
        }
        error = error is null ? null : Redact(error);
        await Append(status == DeploymentStatus.Succeeded ? "Готово. Код завершения: 0." : error ?? "Выполнение завершено с ошибкой.");
        await using var finish = await factory.CreateDbContextAsync();
        await finish.Deployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.Status, status).SetProperty(d => d.ExitCode, exit)
            .SetProperty(d => d.ErrorMessage, error)
            .SetProperty(d => d.Stage, DeploymentDiagnostics.StageName(stage))
            .SetProperty(d => d.FinishedAt, DateTimeOffset.UtcNow)
            .SetProperty(d => d.ProtectedSnapshot, ""));

        async Task WatchCancellation(DeploymentProject project)
        {
            while (!watcher.IsCancellationRequested)
            {
                await using var db = await factory.CreateDbContextAsync(watcher.Token);
                var cancel = await db.Deployments.AsNoTracking().Where(d => d.Id == deployment.Id)
                    .Select(d => d.CancelRequested).FirstAsync(watcher.Token);
                if (cancel)
                {
                    requested = true;
                    await Append("Пользователь запросил остановку. Завершаем команду на сервере…");
                    // Finish an initiated stop even if the command exits while TERM is being handled.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    try { await ssh.StopRunAsync(project, deployment.Id, timeout.Token); stopConfirmed = true; }
                    catch (Exception e)
                    {
                        stopError = e is DomainException ? e.Message : $"Ошибка SSH при остановке ({e.GetType().Name}).";
                    }
                    execution.Cancel();
                    return;
                }
                await Task.Delay(500, watcher.Token);
            }
        }
    }
}
