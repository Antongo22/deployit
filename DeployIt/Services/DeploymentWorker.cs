using System.Text.Json;
using System.Text.RegularExpressions;
using DeployIt.Data;
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
                .SetProperty(d => d.Log, d => d.Log + "\nПанель была перезапущена. Результат удалённой команды неизвестен; проверьте сервер.\n"), stoppingToken);
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
                // No request bodies, tokens, keys, or command output enter application logs.
                logger.LogError("Deployment queue error: {ExceptionType}", e.GetType().Name);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    private async Task RunAsync(Deployment deployment, CancellationToken ct)
    {
        using var logLock = new SemaphoreSlim(1, 1);
        var sensitive = new List<string>();
        async Task Append(string line)
        {
            foreach (var value in sensitive.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
                line = line.Replace(value, "[скрыто]", StringComparison.Ordinal);
            await logLock.WaitAsync(CancellationToken.None);
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var run = await db.Deployments.FindAsync(deployment.Id);
                if (run is null) return;
                var commit = Regex.Match(line, "^DEPLOYIT_COMMIT=([a-f0-9]{40,64})$");
                if (commit.Success && run.CommitSha is null) run.CommitSha = commit.Groups[1].Value;
                if (!run.LogTruncated)
                {
                    if (run.Log.Length + line.Length + 1 > MaxLogLength)
                    { run.Log += "\n[Журнал ограничен 1 МБ.]\n"; run.LogTruncated = true; }
                    else run.Log += line + "\n";
                }
                await db.SaveChangesAsync();
            }
            finally { logLock.Release(); }
        }
        var status = DeploymentStatus.Failed;
        int? exit = null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<DeploymentSnapshot>(secrets.Unprotect(deployment.ProtectedSnapshot))
                ?? throw new InvalidOperationException("Invalid deployment snapshot");
            sensitive.Add(secrets.Unprotect(snapshot.Connection.ProtectedToken));
            sensitive.AddRange(secrets.Unprotect(snapshot.Project.ProtectedPrivateKey).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 10));
            sensitive.Add(secrets.Unprotect(snapshot.Project.ProtectedPassphrase));
            exit = await ssh.DeployAsync(snapshot, deployment.Id, Append, ct);
            status = exit == 0 ? DeploymentStatus.Succeeded : DeploymentStatus.Failed;
            await Append(exit == 0 ? "Готово." : exit is 124 or 137
                ? "Превышено время деплоя. Удалённая команда остановлена timeout." : $"Команда завершилась с кодом {exit}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = DeploymentStatus.Interrupted;
            await Append("Панель остановлена. Удалённая команда может продолжаться до таймаута; проверьте сервер.");
        }
        catch (Exception e)
        {
            await Append($"Не удалось выполнить деплой ({e.GetType().Name}). Проверьте доступ к Git, SSH-ключ, отпечаток и журнал сервера.");
        }
        await using var finish = await factory.CreateDbContextAsync();
        await finish.Deployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.Status, status).SetProperty(d => d.ExitCode, exit)
            .SetProperty(d => d.FinishedAt, DateTimeOffset.UtcNow)
            .SetProperty(d => d.ProtectedSnapshot, ""));
    }
}
