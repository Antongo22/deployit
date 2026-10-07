using System.Text;
using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace DeployIt.Services;

public sealed partial class SshDeploymentService(SecretProtector secrets)
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
        catch (SshAuthenticationException) { throw AuthenticationError(project); }
        catch (Exception) when (!ct.IsCancellationRequested)
        { throw new DomainException("SSH-проверка не прошла. Проверьте адрес, порт, способ входа и отпечаток сервера. Пример подключения: ssh -p 22 deploy@app.example.com."); }
    }

    public async Task<int> DeployAsync(DeploymentSnapshot snapshot, Guid runId,
        Func<string, Task> log, CancellationToken ct)
    {
        var project = snapshot.Project;
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        Pin(client, project.HostFingerprint);
        await log("DEPLOYIT_STAGE=ssh");
        await log("Подключение к SSH-серверу…");
        try { await client.ConnectAsync(ct); }
        catch (SshAuthenticationException) { throw AuthenticationError(project); }
        var credentialsDirectory = "/tmp/deployit-" + runId.ToString("N");
        var token = snapshot.Connection.PublicOnly ? "" : secrets.Unprotect(snapshot.Connection.ProtectedToken);
        try
        {
            if (token != "")
            {
                await log("DEPLOYIT_STAGE=credentials");
                using var sftp = new SftpClient(Connection(project, key));
                Pin(sftp, project.HostFingerprint);
                await sftp.ConnectAsync(ct);
                sftp.CreateDirectory(credentialsDirectory);
                // SSH.NET expects octal digits (700), not a decimal bit mask (448).
                sftp.ChangePermissions(credentialsDirectory, 700);
                Upload(sftp, credentialsDirectory + "/token", token, 600);
                var gitUsername = snapshot.Connection.Provider == GitProvider.GitHub ? snapshot.Connection.Account : "oauth2";
                Upload(sftp, credentialsDirectory + "/askpass", "#!/bin/sh\ncase \"$1\" in\n*Username*) printf '%s' " + Quote(gitUsername) + " ;;\n*) cat "
                    + Quote(credentialsDirectory + "/token") + " ;;\nesac\n", 700);
            }
            using var command = client.CreateCommand(BuildCommand(project, runId, token != ""));
            command.CommandTimeout = TimeSpan.FromMinutes(project.TimeoutMinutes) + TimeSpan.FromSeconds(45);
            await log("Получение кода и выполнение команды деплоя…");
            var execute = command.ExecuteAsync(ct);
            var stdout = ReadAsync(command.OutputStream, log, ct);
            var stderr = ReadAsync(command.ExtendedOutputStream, line => log("[stderr] " + line), ct);
            await Task.WhenAll(execute, stdout, stderr);
            return CommandExitCode(command);
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
        var script = $$"""
            {{RunPrelude(project, runId, credentialDirectory)}}
            export GIT_TERMINAL_PROMPT=0
            export GIT_ASKPASS={{Quote(credentials ? credentialDirectory + "/askpass" : "/bin/false")}}
            mkdir -p -- {{Quote(project.WorkingDirectory + "/releases")}}
            exec 9>{{Quote(project.WorkingDirectory + "/.deployit.lock")}}
            flock -n 9 || { printf '%s\n' 'Другой деплой ещё выполняется на сервере.'; exit 75; }
            phase=clone; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            check_cancel
            printf '%s\n' 'Клонирование выбранной ветки…'
            LC_ALL=C git -c credential.helper= -c http.followRedirects=false clone --depth 1 --branch {{Quote(project.Branch)}} -- {{Quote(project.RepositoryUrl)}} {{Quote(release)}} 9>&-
            cd -- {{Quote(release)}}
            printf 'DEPLOYIT_COMMIT=%s\n' "$(git rev-parse HEAD)"
            phase=environment; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            check_cancel
            {{EnvironmentLink(project)}}
            export DEPLOYIT_RELEASE_DIR={{Quote(release)}}
            export DEPLOYIT_PROJECT_DIR={{Quote(project.WorkingDirectory)}}
            export DEPLOYIT_ENV_FILE={{Quote(project.WorkingDirectory + "/.env")}}
            export COMPOSE_PROJECT_NAME=deployit-{{project.Id:N}}
            phase=deploy-command; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            check_cancel
            printf '%s\n' 'Выполнение команды деплоя…'
            bash -lc {{Quote(project.DeployCommand)}} 9>&-
            phase=publish; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            check_cancel
            ln -s -- {{Quote(release)}} {{Quote(project.WorkingDirectory + "/.current-" + runId.ToString("N"))}}
            mv -Tf -- {{Quote(project.WorkingDirectory + "/.current-" + runId.ToString("N"))}} {{Quote(project.WorkingDirectory + "/current")}}
            phase=complete; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            printf '%s\n' 'Деплой завершён успешно.'
            """;
        return $"timeout --signal=TERM --kill-after=15s {project.TimeoutMinutes * 60}s bash --noprofile --norc -c {Quote(script)}";
    }

    public async Task<int> RestartAsync(DeploymentSnapshot snapshot, Func<string, Task> log, CancellationToken ct, Guid? runId = null)
        => await RunCurrentReleaseAsync(snapshot, runId ?? Guid.NewGuid(), DeploymentOperation.Restart, log, ct);

    public async Task<int> StopAsync(DeploymentSnapshot snapshot, Guid runId, Func<string, Task> log, CancellationToken ct)
        => await RunCurrentReleaseAsync(snapshot, runId, DeploymentOperation.Stop, log, ct);

    private async Task<int> RunCurrentReleaseAsync(DeploymentSnapshot snapshot, Guid runId,
        DeploymentOperation operation, Func<string, Task> log, CancellationToken ct)
    {
        var project = snapshot.Project;
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        Pin(client, project.HostFingerprint);
        await log("DEPLOYIT_STAGE=ssh");
        await log(operation == DeploymentOperation.Stop
            ? "Подключение к SSH-серверу для остановки проекта…" : "Подключение к SSH-серверу для перезапуска…");
        try { await client.ConnectAsync(ct); }
        catch (SshAuthenticationException) { throw AuthenticationError(project); }
        using var command = client.CreateCommand(BuildCurrentReleaseCommand(project, runId, operation));
        command.CommandTimeout = TimeSpan.FromMinutes(project.TimeoutMinutes) + TimeSpan.FromSeconds(45);
        await log(operation == DeploymentOperation.Stop
            ? "Остановка текущего релиза…" : "Перезапуск текущего релиза без загрузки кода из репозитория…");
        var execute = command.ExecuteAsync(ct);
        await Task.WhenAll(execute, ReadAsync(command.OutputStream, log, ct),
            ReadAsync(command.ExtendedOutputStream, line => log("[stderr] " + line), ct));
        return CommandExitCode(command);
    }

    public static string BuildRestartCommand(DeploymentProject project, Guid? runId = null)
        => BuildCurrentReleaseCommand(project, runId ?? Guid.NewGuid(), DeploymentOperation.Restart);

    public static string BuildStopCommand(DeploymentProject project, Guid? runId = null)
        => BuildCurrentReleaseCommand(project, runId ?? Guid.NewGuid(), DeploymentOperation.Stop);

    private static string BuildCurrentReleaseCommand(DeploymentProject project, Guid runId, DeploymentOperation operation)
    {
        var stopping = operation == DeploymentOperation.Stop;
        var script = $$"""
            {{RunPrelude(project, runId)}}
            root={{Quote(project.WorkingDirectory)}}
            if [ ! -d "$root/current" ]; then
                printf '%s\n' 'Текущий релиз не найден. Сначала нажмите «Развернуть».' >&2
                exit 66
            fi
            exec 9>"$root/.deployit.lock"
            flock -n 9 || { printf '%s\n' 'Другой деплой, перезапуск, остановка или сохранение .env ещё выполняется.' >&2; exit 75; }
            cd -- "$root/current"
            export DEPLOYIT_RELEASE_DIR="$(pwd -P)"
            export DEPLOYIT_PROJECT_DIR="$root"
            export DEPLOYIT_ENV_FILE="$root/.env"
            export COMPOSE_PROJECT_NAME=deployit-{{project.Id:N}}
            phase=environment; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            {{EnvironmentLink(project)}}
            if git rev-parse HEAD >/dev/null 2>&1; then printf 'DEPLOYIT_COMMIT=%s\n' "$(git rev-parse HEAD)"; fi
            phase={{(stopping ? "stop-command" : "restart-command")}}; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            check_cancel
            bash -lc {{Quote(stopping ? project.StopCommand : project.RestartCommand)}} 9>&-
            phase=complete; printf 'DEPLOYIT_STAGE=%s\n' "$phase"
            printf '%s\n' {{Quote(stopping ? "Проект остановлен. Для запуска нажмите «Перезапустить» или «Развернуть»." : "Перезапуск завершён успешно.")}}
            """;
        return $"timeout --signal=TERM --kill-after=15s {project.TimeoutMinutes * 60}s bash --noprofile --norc -c {Quote(script)}";
    }

    private static string EnvironmentLink(DeploymentProject project) =>
        $"if [ -f {Quote(project.WorkingDirectory + "/.env")} ]; then ln -sfnT -- {Quote(project.WorkingDirectory + "/.env")} .env; fi";

    private static int CommandExitCode(SshCommand command) => command.ExitStatus ?? command.ExitSignal switch {
        "KILL" => 137, "TERM" => 143, "HUP" => 129, "INT" => 130, _ => -1
    };

    private PrivateKeyFile? LoadKey(DeploymentProject project) => project.AuthenticationType == SshAuthenticationType.PrivateKey ? new(
        new MemoryStream(Encoding.UTF8.GetBytes(secrets.Unprotect(project.ProtectedPrivateKey))),
        string.IsNullOrEmpty(project.ProtectedPassphrase) ? null : secrets.Unprotect(project.ProtectedPassphrase)) : null;
    private ConnectionInfo Connection(DeploymentProject p, PrivateKeyFile? key)
    {
        AuthenticationMethod authentication = p.AuthenticationType switch {
            SshAuthenticationType.Password => new PasswordAuthenticationMethod(p.Username, secrets.Unprotect(p.ProtectedPassword)),
            SshAuthenticationType.PrivateKey when key is not null => new PrivateKeyAuthenticationMethod(p.Username, key),
            _ => throw new DomainException("Не настроен способ входа по SSH. В настройках проекта выберите «SSH-ключ» или «Логин и пароль» и заполните соответствующие поля.")
        };
        return new ConnectionInfo(p.Host, p.Port, p.Username, authentication) { Timeout = TimeSpan.FromSeconds(15) };
    }
    private static DomainException AuthenticationError(DeploymentProject project) => new(
        project.AuthenticationType == SshAuthenticationType.Password
            ? "SSH-сервер отклонил вход по паролю. Проверьте логин и пароль пользователя; на сервере должен быть разрешён вход по паролю. Пример: для ssh deploy@app.example.com укажите SSH-пользователя deploy и его пароль на сервере."
            : "SSH-сервер отклонил вход по ключу. Проверьте SSH-пользователя и ключ. Например, для пользователя deploy публичная часть ключа должна находиться в /home/deploy/.ssh/authorized_keys.");
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
