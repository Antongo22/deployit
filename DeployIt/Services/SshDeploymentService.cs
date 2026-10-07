using System.Text;
using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace DeployIt.Services;

public sealed class SshDeploymentService(SecretProtector secrets)
{
    public async Task<string> ProbeAsync(string host, int port, CancellationToken ct = default)
    {
        ProjectService.ValidateServer(host, port);
        string? fingerprint = null;
        using var client = new SshClient(new ConnectionInfo(host, port, "deployit-probe",
            new PasswordAuthenticationMethod("deployit-probe", "unused")) { Timeout = TimeSpan.FromSeconds(10) });
        client.HostKeyReceived += (_, e) => { fingerprint = "SHA256:" + e.FingerPrintSHA256; e.CanTrust = false; };
        try { await client.ConnectAsync(ct); }
        catch (Exception) when (fingerprint is not null) { /* Deliberately reject the key before authentication. */ }
        catch (Exception) when (!ct.IsCancellationRequested) { throw new DomainException("SSH-сервер недоступен. Проверьте адрес и порт."); }
        return fingerprint ?? throw new DomainException("Не удалось получить SSH-ключ сервера.");
    }

    public async Task<string> CheckAsync(DeploymentProject project, CancellationToken ct = default)
    {
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        Pin(client, project.HostFingerprint);
        try
        {
            await client.ConnectAsync(ct);
            using var command = client.CreateCommand("bash -lc 'set -e; command -v git; command -v bash; command -v timeout; command -v flock; printf SSH_OK'");
            command.CommandTimeout = TimeSpan.FromSeconds(15);
            await command.ExecuteAsync(ct);
            if (command.ExitStatus != 0) throw new DomainException("На сервере нужны bash, git, timeout и flock.");
            return "SSH подключён. Git, bash, timeout и flock доступны.";
        }
        catch (DomainException) { throw; }
        catch (Exception) when (!ct.IsCancellationRequested)
        { throw new DomainException("SSH-проверка не прошла. Проверьте пользователя, ключ и отпечаток сервера."); }
    }

    public async Task<int> DeployAsync(DeploymentSnapshot snapshot, Guid runId,
        Func<string, Task> log, CancellationToken ct)
    {
        var project = snapshot.Project;
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        Pin(client, project.HostFingerprint);
        await log("Подключение к SSH-серверу…");
        await client.ConnectAsync(ct);
        var credentialsDirectory = "/tmp/deployit-" + runId.ToString("N");
        var token = snapshot.Connection.PublicOnly ? "" : secrets.Unprotect(snapshot.Connection.ProtectedToken);
        try
        {
            if (token != "")
            {
                using var sftp = new SftpClient(Connection(project, key));
                Pin(sftp, project.HostFingerprint);
                await sftp.ConnectAsync(ct);
                sftp.CreateDirectory(credentialsDirectory);
                sftp.ChangePermissions(credentialsDirectory, 448); // 0700
                Upload(sftp, credentialsDirectory + "/token", token, 384); // 0600
                var gitUsername = snapshot.Connection.Provider == GitProvider.GitHub ? snapshot.Connection.Account : "oauth2";
                Upload(sftp, credentialsDirectory + "/askpass", "#!/bin/sh\ncase \"$1\" in\n*Username*) printf '%s' " + Quote(gitUsername) + " ;;\n*) cat "
                    + Quote(credentialsDirectory + "/token") + " ;;\nesac\n", 448);
            }
            using var command = client.CreateCommand(BuildCommand(project, runId, token != ""));
            command.CommandTimeout = TimeSpan.FromMinutes(project.TimeoutMinutes) + TimeSpan.FromSeconds(45);
            await log("Получение кода и выполнение команды деплоя…");
            var execute = command.ExecuteAsync(ct);
            var stdout = ReadAsync(command.OutputStream, log, ct);
            var stderr = ReadAsync(command.ExtendedOutputStream, line => log("[stderr] " + line), ct);
            await Task.WhenAll(execute, stdout, stderr);
            return command.ExitStatus ?? -1;
        }
        finally
        {
            // The shell trap also removes these files if the app loses the SSH connection.
            if (token != "" && client.IsConnected)
            {
                try
                {
                    using var cleanup = client.CreateCommand("rm -rf -- " + Quote(credentialsDirectory));
                    cleanup.CommandTimeout = TimeSpan.FromSeconds(10);
                    await cleanup.ExecuteAsync(CancellationToken.None);
                }
                catch { /* Remote trap is the second cleanup path. */ }
            }
        }
    }

    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public static string BuildCommand(DeploymentProject project, Guid runId, bool credentials)
    {
        var credentialDirectory = "/tmp/deployit-" + runId.ToString("N");
        var release = project.WorkingDirectory + "/releases/" + runId.ToString("N");
        var cleanup = Quote("rm -rf -- " + Quote(credentialDirectory));
        var script = $$"""
            set -euo pipefail
            umask 077
            trap {{cleanup}} EXIT
            export GIT_TERMINAL_PROMPT=0
            export GIT_ASKPASS={{Quote(credentials ? credentialDirectory + "/askpass" : "/bin/false")}}
            mkdir -p -- {{Quote(project.WorkingDirectory + "/releases")}}
            exec 9>{{Quote(project.WorkingDirectory + "/.deployit.lock")}}
            flock -n 9 || { printf '%s\n' 'Другой деплой ещё выполняется на сервере.'; exit 75; }
            printf '%s\n' 'Клонирование выбранной ветки…'
            LC_ALL=C git -c credential.helper= -c http.followRedirects=false clone --depth 1 --branch {{Quote(project.Branch)}} -- {{Quote(project.RepositoryUrl)}} {{Quote(release)}} 9>&-
            cd -- {{Quote(release)}}
            printf 'DEPLOYIT_COMMIT=%s\n' "$(git rev-parse HEAD)"
            export DEPLOYIT_RELEASE_DIR={{Quote(release)}}
            export DEPLOYIT_PROJECT_DIR={{Quote(project.WorkingDirectory)}}
            export COMPOSE_PROJECT_NAME=deployit-{{project.Id:N}}
            printf '%s\n' 'Выполнение команды деплоя…'
            bash -lc {{Quote(project.DeployCommand)}} 9>&-
            ln -s -- {{Quote(release)}} {{Quote(project.WorkingDirectory + "/.current-" + runId.ToString("N"))}}
            mv -Tf -- {{Quote(project.WorkingDirectory + "/.current-" + runId.ToString("N"))}} {{Quote(project.WorkingDirectory + "/current")}}
            printf '%s\n' 'Деплой завершён успешно.'
            """;
        return $"timeout --signal=TERM --kill-after=15s {project.TimeoutMinutes * 60}s bash -lc {Quote(script)}";
    }

    private PrivateKeyFile LoadKey(DeploymentProject project) => new(
        new MemoryStream(Encoding.UTF8.GetBytes(secrets.Unprotect(project.ProtectedPrivateKey))),
        string.IsNullOrEmpty(project.ProtectedPassphrase) ? null : secrets.Unprotect(project.ProtectedPassphrase));
    private static ConnectionInfo Connection(DeploymentProject p, PrivateKeyFile key) => new(p.Host, p.Port,
        p.Username, new PrivateKeyAuthenticationMethod(p.Username, key)) { Timeout = TimeSpan.FromSeconds(15) };
    private static void Pin(BaseClient client, string fingerprint) => client.HostKeyReceived += (_, e) =>
        e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == fingerprint;
    private static void Upload(SftpClient client, string path, string content, short mode)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        client.UploadFile(stream, path);
        client.ChangePermissions(path, mode);
    }
    private static async Task ReadAsync(Stream stream, Func<string, Task> log, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line) await log(line);
    }
}
