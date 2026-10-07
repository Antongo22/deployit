using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeployIt.Services;

public sealed partial class SshDeploymentService
{
    public const int MaxEnvironmentCharacters = 32768;
    private const int MaxEnvironmentBytes = 65536;
    private static readonly UTF8Encoding EnvironmentEncoding = new(false, true);

    public async Task<EnvironmentFileView> ReadEnvironmentAsync(DeploymentProject project, CancellationToken ct = default)
    {
        using var key = LoadKey(project);
        using var client = new SftpClient(Connection(project, key)) { OperationTimeout = TimeSpan.FromSeconds(30) };
        Pin(client, project.HostFingerprint);
        var path = project.WorkingDirectory + "/.env";
        try
        {
            await client.ConnectAsync(ct);
            var file = await ReadEnvironmentFile(client, path, ct);
            return file with { Version = TargetVersion(project) + ":" + file.Version };
        }
        catch (DomainException) { throw; }
        catch (SshAuthenticationException) { throw AuthenticationError(project); }
        catch (Exception) when (!ct.IsCancellationRequested)
        { throw new DomainException("Не удалось загрузить .env. Проверьте SSH/SFTP и права пользователя на файл. Пример пути: /opt/apps/my-app/.env."); }
    }

    public async Task<EnvironmentFileView> SaveEnvironmentAsync(DeploymentProject project, EnvironmentFileInput input,
        CancellationToken ct = default)
    {
        var version = input.Version;
        var prefix = TargetVersion(project) + ":";
        if (version is null || !version.StartsWith(prefix, StringComparison.Ordinal))
            throw new DomainException("Настройки SSH-сервера или каталога проекта изменились. Загрузите .env с сервера заново перед сохранением.", 409);
        version = version[prefix.Length..];
        if (version != "missing" && !Regex.IsMatch(version, "^[a-f0-9]{64}$"))
            throw new DomainException("Сначала загрузите .env с сервера, затем сохраните изменения.");
        if (input.Content is null || input.Content.Length > MaxEnvironmentCharacters
            || input.Content.Contains('\0') || EnvironmentEncoding.GetByteCount(input.Content) > MaxEnvironmentBytes)
            throw new DomainException("Файл .env должен содержать UTF-8 текст до 32 768 символов и 64 КБ, без нулевых символов. Пример: APP_ENV=production.");

        using var key = LoadKey(project);
        using var ssh = new SshClient(Connection(project, key));
        Pin(ssh, project.HostFingerprint);
        var path = project.WorkingDirectory + "/.env";
        var temporary = project.WorkingDirectory + "/.env.deployit-" + Guid.NewGuid().ToString("N");
        var bytes = EnvironmentEncoding.GetBytes(input.Content);
        var desiredVersion = Version(bytes);
        var stage = "подключение по SSH";
        try
        {
            await ssh.ConnectAsync(ct);
            // Keep file creation, input transfer and commit in the same SSH session.
            // Contents travel on stdin, never in command arguments or shell history.
            var script = $$"""
                set -euo pipefail
                umask 077
                root={{Quote(project.WorkingDirectory)}}
                file={{Quote(path)}}
                temporary={{Quote(temporary)}}
                link_file=
                trap 'rm -f -- "$temporary"; if [ -n "$link_file" ]; then rm -f -- "$link_file"; fi' EXIT
                mkdir -p -- "$root" || exit 77
                (set -o noclobber; cat > "$temporary") || exit 78
                command -v sha256sum >/dev/null || exit 80
                received=$(sha256sum -- "$temporary"); received=${received%% *}
                [ "$received" = {{Quote(desiredVersion)}} ] || exit 76
                exec 9>"$root/.deployit.lock" || exit 79
                command -v flock >/dev/null || exit 81
                flock -n 9 || exit 75
                if [ -L "$file" ] || { [ -e "$file" ] && [ ! -f "$file" ]; }; then exit 70; fi
                actual=missing
                if [ -f "$file" ]; then actual=$(sha256sum -- "$file"); actual=${actual%% *}; fi
                [ "$actual" = {{Quote(version)}} ] || exit 73
                if [ -d "$root/current" ]; then
                    root_real=$(readlink -f -- "$root")
                    current=$(readlink -f -- "$root/current")
                    case "$current" in "$root_real"/releases/*) ;; *) exit 72 ;; esac
                    [ ! -d "$current/.env" ] || exit 71
                    link_file="$current/.env.deployit-link-{{Guid.NewGuid():N}}"
                    ln -s -- "$file" "$link_file"
                fi
                mv -f -- "$temporary" "$file"
                if [ -n "$link_file" ]; then mv -Tf -- "$link_file" "$current/.env" || exit 74; fi
                """;
            using var save = ssh.CreateCommand("bash --noprofile --norc -c " + Quote(script));
            save.CommandTimeout = TimeSpan.FromSeconds(30);
            stage = "передача файла";
            var execute = save.ExecuteAsync(ct);
            Exception? transferError = null;
            try
            {
                using var stdin = save.CreateInputStream();
                await stdin.WriteAsync(bytes, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { transferError = e; }
            // Observe execution even when the server rejects input early. Its exit code
            // gives a useful error; a digest check prevents committing partial input.
            stage = "подтверждение сохранения";
            await execute;
            switch (save.ExitStatus)
            {
                case 0: break;
                case 73: throw new DomainException(".env изменился на сервере после загрузки. Ваш текст сохранён в редакторе. Загрузите серверную версию и объедините изменения перед сохранением.", 409);
                case 75: throw new DomainException("Сейчас выполняется деплой, перезапуск или другое сохранение .env. Дождитесь завершения и повторите сохранение.", 409);
                case 70: throw new DomainException("Путь .env на сервере должен быть обычным файлом, а не каталогом или символической ссылкой. Пример: /opt/apps/my-app/.env.");
                case 71: throw new DomainException("В текущем релизе .env является каталогом. Нужен обычный файл .env. Изменения не сохранены.");
                case 72: throw new DomainException("Текущий релиз находится вне каталога releases этого проекта. Изменения не сохранены; проверьте ссылку current в корневом каталоге проекта.");
                case 74: throw new DomainException(".env сохранён в корневом каталоге проекта, но обновить ссылку в текущем релизе не удалось. Проверьте права на текущий релиз и нажмите «Перезапустить».");
                case 76: throw new DomainException("Передан не весь файл .env. Серверная версия не изменена. Повторите сохранение после восстановления SSH-соединения.");
                case 77: throw new DomainException($"Не удалось создать каталог {project.WorkingDirectory}. Проверьте права SSH-пользователя. Пример проверки: ls -ld -- {Quote(project.WorkingDirectory)}.");
                case 78: throw new DomainException($"Не удалось записать временный .env в {project.WorkingDirectory}. Проверьте права на каталог и свободное место: df -h -- {Quote(project.WorkingDirectory)}.");
                case 79: throw new DomainException("Нет доступа к файлу .deployit.lock в каталоге проекта. SSH-пользователю нужны права записи в этот каталог.");
                case 80: throw new DomainException("На сервере отсутствует sha256sum. Например, в Debian/Ubuntu установите coreutils: sudo apt-get install coreutils.");
                case 81: throw new DomainException("На сервере отсутствует flock. Например, в Debian/Ubuntu установите util-linux: sudo apt-get install util-linux.");
                default:
                    if (transferError is not null) throw transferError;
                    throw new DomainException($"Не удалось сохранить .env (код команды: {save.ExitStatus?.ToString() ?? "не получен"}). Проверьте bash и права на каталог проекта. Пример: ls -ld -- {Quote(project.WorkingDirectory)}.");
            }
            return new(path, true, input.Content, prefix + desiredVersion);
        }
        catch (DomainException) { throw; }
        catch (SshAuthenticationException) { throw AuthenticationError(project); }
        catch (SshOperationTimeoutException)
        { throw new DomainException($"Сохранение .env: превышено время ожидания на этапе «{stage}». Загрузите файл с сервера перед повторной попыткой. Пример проверки подключения: ssh -p {project.Port} {project.Username}@{project.Host}."); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            throw new DomainException($"Сохранение .env прервано на этапе «{stage}» ({e.GetType().Name}). Загрузите файл с сервера перед повторной попыткой. Пример проверки: ssh -p {project.Port} {project.Username}@{project.Host}.");
        }
    }

    private static async Task<EnvironmentFileView> ReadEnvironmentFile(SftpClient client, string path, CancellationToken ct)
    {
        if (!client.Exists(path)) return new(path, false, "", "missing");
        var attributes = client.GetAttributes(path);
        if (!attributes.IsRegularFile)
            throw new DomainException("По пути .env нужен обычный текстовый файл. Пример: /opt/apps/my-app/.env.");
        if (attributes.Size > MaxEnvironmentBytes)
            throw new DomainException("Файл .env больше 64 КБ. Уменьшите его размер перед редактированием в панели.");
        using var input = client.OpenRead(path);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > MaxEnvironmentBytes)
                throw new DomainException("Файл .env больше 64 КБ. Уменьшите его размер перед редактированием в панели.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        string content;
        try { content = EnvironmentEncoding.GetString(bytes); }
        catch (DecoderFallbackException) { throw new DomainException("Файл .env должен быть в кодировке UTF-8. Пример строки: APP_ENV=production."); }
        if (content.StartsWith('\uFEFF')) content = content[1..];
        if (content.Length > MaxEnvironmentCharacters || content.Contains('\0'))
            throw new DomainException("Файл .env должен содержать текст до 32 768 символов без нулевых символов. Пример: APP_ENV=production.");
        return new(path, true, content, Version(bytes));
    }

    private static string Version(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string TargetVersion(DeploymentProject project) => Version(EnvironmentEncoding.GetBytes(
        $"{project.Id:N}\n{project.Host}\n{project.Port}\n{project.Username}\n{project.HostFingerprint}\n{project.WorkingDirectory}"));
}
