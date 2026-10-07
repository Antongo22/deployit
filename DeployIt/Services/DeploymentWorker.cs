using System.Text.Json;
using System.Text.RegularExpressions;
using DeployIt.Data;
using DeployIt.DTOs;
using DeployIt.Models;
using Microsoft.EntityFrameworkCore;

namespace DeployIt.Services;

public sealed class DeploymentWorker(IDbContextFactory<DeployItDbContext> factory,
    SshDeploymentService ssh, SecretProtector secrets, ILogger<DeploymentWorker> logger) : BackgroundService
{
    private const int MaxLogLength = 1_000_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await factory.CreateDbContextAsync(stoppingToken))
        {
            await db.Deployments.Where(d => d.Status == DeploymentStatus.Running).ExecuteUpdateAsync(update => update
                .SetProperty(d => d.Status, DeploymentStatus.Interrupted)
                .SetProperty(d => d.FinishedAt, DateTimeOffset.UtcNow)
                .SetProperty(d => d.ProtectedSnapshot, "")
                .SetProperty(d => d.ErrorMessage, "Панель была перезапущена. Результат удалённой команды неизвестен; проверьте сервер перед повторным запуском.")
                .SetProperty(d => d.Log, d => d.Log + "\nПанель была перезапущена. Проверьте сервер.\n"), stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var next = await db.Deployments.AsNoTracking().Where(d => d.Status == DeploymentStatus.Queued)
                    .OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                if (next is null) { await Task.Delay(1000, stoppingToken); continue; }
                var claimed = await db.Deployments.Where(d => d.Id == next.Id && d.Status == DeploymentStatus.Queued)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DeploymentStatus.Running)
                        .SetProperty(d => d.StartedAt, DateTimeOffset.UtcNow), stoppingToken);
                if (claimed == 1) await RunAsync(next, stoppingToken);
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

    private async Task RunAsync(Deployment deployment, CancellationToken stoppingToken)
    {
        using var logLock = new SemaphoreSlim(1, 1);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sensitive = new List<string>();
        var stage = "ssh";
        var tail = "";
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
            exit = deployment.Operation == DeploymentOperation.Restart
                ? await ssh.RestartAsync(snapshot, Append, execution.Token, deployment.Id)
                : await ssh.DeployAsync(snapshot, deployment.Id, Append, execution.Token);
            status = exit == 0 ? DeploymentStatus.Succeeded : DeploymentStatus.Failed;
            if (exit != 0) error = DeploymentDiagnostics.Failure(exit, stage, tail, snapshot.Project.TimeoutMinutes);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            status = DeploymentStatus.Interrupted;
            error = "Выполнение прервано. Результат удалённой команды неизвестен; проверьте сервер.";
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
