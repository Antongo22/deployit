using System.Net.Sockets;
using System.Text;
using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeployIt.Services;

public sealed class RemoteOutcomeUnknownException(string message) : Exception(message);

public sealed partial class SshDeploymentService
{
    private static string JobDirectory(DeploymentProject project, Guid id) => project.WorkingDirectory + "/.deployit-runs/" + id.ToString("N");

    public static string BuildLaunchCommand(DeploymentProject project, Guid id, string operation)
    {
        ValidateStoragePath(project.WorkingDirectory);
        var directory = JobDirectory(project, id);
        var worker = $$"""
            set -u
            umask 077
            job={{Quote(directory)}}
            mkfifo "$job/stderr"
            sed -u 's/^/[stderr] /' < "$job/stderr" &
            stderr_pid=$!
            {{operation}} 2> "$job/stderr"
            result=$?
            wait "$stderr_pid"
            rm -f -- "$job/stderr"
            printf '%s\n' "$result" > "$job/exit.tmp"
            mv -fT -- "$job/exit.tmp" "$job/exit"
            """;
        var script = $$"""
            set -euo pipefail
            umask 077
            root={{Quote(project.WorkingDirectory)}}
            mkdir -p -- "$root"
            {{CheckStorageRoot(project)}}
            jobs="$root/.deployit-runs"
            mkdir -p -- "$jobs"
            [ ! -L "$jobs" ] && [ "$(readlink -f -- "$jobs")" = "$jobs" ] || exit 76
            job={{Quote(directory)}}
            mkdir -p -- "$job"
            [ ! -L "$job" ] && [ "$(readlink -f -- "$job")" = "$job" ] || exit 76
            exec 8>"$job/launch.lock"
            flock -w 5 8 || exit 75
            # A lost SSH acknowledgement may repeat this request. A claimed run is NEVER started twice.
            if [ -e "$job/worker.sh" ] || [ -L "$job/worker.sh" ]; then exit 0; fi
            [ ! -e "$job/output" ] && [ ! -L "$job/output" ] || exit 76
            (set -o noclobber; printf '%s\n' {{Quote(worker)}} > "$job/worker.sh")
            nohup setsid bash --noprofile --norc "$job/worker.sh" < /dev/null > "$job/output" 2>&1 8>&- &
            printf '%s\n' "$!" > "$job/pid"
            """;
        return "bash --noprofile --norc -c " + Quote(script);
    }

    public static string BuildReadRunCommand(DeploymentProject project, Guid id, long offset)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ValidateStoragePath(project.WorkingDirectory);
        var script = $$"""
            set -euo pipefail
            job={{Quote(JobDirectory(project, id))}}
            if [ -e "$job" ] || [ -L "$job" ]; then
                [ ! -L "$job" ] && [ "$(readlink -f -- "$job")" = "$job" ] || exit 76
                for file in output exit pid worker.sh; do [ ! -L "$job/$file" ] || exit 76; done
            fi
            state=absent
            if [ -f "$job/exit" ]; then
                read -r code < "$job/exit"
                [[ "$code" =~ ^[0-9]{1,3}$ ]] && [ "$code" -le 255 ] || exit 83
                state="done:$code"
            elif [ -f "$job/worker.sh" ]; then
                state=unknown
                if [ -f "$job/pid" ]; then
                    read -r pid < "$job/pid"
                    if [[ "$pid" =~ ^[0-9]+$ ]] && [ "$pid" -gt 1 ] && [ -r "/proc/$pid/cmdline" ]; then
                        cmdline=$(tr '\0' ' ' < "/proc/$pid/cmdline")
                        case "$cmdline" in *"$job/worker.sh"*) state=running ;; esac
                    fi
                fi
            fi
            size=0
            if [ -f "$job/output" ]; then size=$(stat -c '%s' -- "$job/output"); fi
            printf '%s|%s\n' "$state" "$size"
            if [ "$size" -gt {{offset}} ]; then
                dd if="$job/output" iflag=skip_bytes,count_bytes skip={{offset}} count=65536 status=none | base64 -w0
            fi
            """;
        return "bash --noprofile --norc -c " + Quote(script);
    }

    public Task<int> ResumeAsync(DeploymentSnapshot snapshot, Guid id, Func<string, Task> log,
        CancellationToken ct, long offset = 0, Func<long, Task>? checkpoint = null)
        => RunResilientAsync(snapshot.Project, id, null, log, ct, offset, checkpoint);

    private async Task<int> RunResilientAsync(DeploymentProject project, Guid id, string? operation,
        Func<string, Task> log, CancellationToken ct, long offset = 0, Func<long, Task>? checkpoint = null)
    {
        using var key = LoadKey(project);
        SshClient? client = null;
        var launched = operation is null;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(project.TimeoutMinutes).AddSeconds(90);
        DateTimeOffset? disconnectedAt = null;
        var pending = new List<byte>();
        var cursor = offset;
        async Task Disconnected() {
            if (disconnectedAt.HasValue) return;
            disconnectedAt = DateTimeOffset.UtcNow;
            await log("Связь по SSH потеряна. Команда была запущена автономно; пробуем подключиться снова до 2 минут и проверяем её результат.");
        }
        try {
            while (DateTimeOffset.UtcNow < deadline) {
                ct.ThrowIfCancellationRequested();
                try {
                    if (client?.IsConnected != true) {
                        if (client is not null && launched) await Disconnected();
                        client?.Dispose();
                        client = new SshClient(Connection(project, key)) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
                        Pin(client, project.HostFingerprint);
                        await client.ConnectAsync(ct);
                        if (disconnectedAt.HasValue) await log("SSH-соединение восстановлено. Читаем результат прежнего запуска; повторный деплой не создаётся.");
                        disconnectedAt = null;
                    }
                    if (!launched) {
                        using var launch = client.CreateCommand(BuildLaunchCommand(project, id, operation!));
                        launch.CommandTimeout = TimeSpan.FromSeconds(20);
                        await launch.ExecuteAsync(ct);
                        if (launch.ExitStatus != 0) throw new DomainException("Не удалось подготовить автономный запуск. Проверьте каталог и наличие nohup, setsid, flock, sed и base64.");
                        launched = true;
                    }
                    using var read = client.CreateCommand(BuildReadRunCommand(project, id, cursor));
                    read.CommandTimeout = TimeSpan.FromSeconds(15);
                    await read.ExecuteAsync(ct);
                    if (read.ExitStatus != 0) throw new RemoteOutcomeUnknownException("Не удалось прочитать результат удалённого запуска. Проверьте сервер перед повторным деплоем.");
                    var parts = read.Result.Split('\n', 2);
                    var header = parts[0].TrimEnd('\r').Split('|');
                    if (header.Length != 2 || !long.TryParse(header[1], out var size) || size < cursor)
                        throw new RemoteOutcomeUnknownException("Журнал удалённого запуска недоступен или изменён. Результат не подтверждён.");
                    var bytes = Convert.FromBase64String(parts.ElementAtOrDefault(1)?.Trim() ?? "");
                    pending.AddRange(bytes); cursor += bytes.Length;
                    var complete = pending.LastIndexOf((byte)'\n');
                    if (complete >= 0) {
                        var lines = Encoding.UTF8.GetString(pending.Take(complete + 1).ToArray()).Split('\n');
                        foreach (var line in lines.SkipLast(1)) await log(line.TrimEnd('\r'));
                        pending.RemoveRange(0, complete + 1);
                        if (checkpoint is not null) await checkpoint(cursor - pending.Count);
                    }
                    if (header[0].StartsWith("done:", StringComparison.Ordinal) && cursor >= size) {
                        if (pending.Count > 0) await log(Encoding.UTF8.GetString(pending.ToArray()));
                        if (checkpoint is not null) await checkpoint(cursor);
                        return int.Parse(header[0][5..]);
                    }
                    if (header[0] is "absent" or "unknown")
                        throw new RemoteOutcomeUnknownException("Сервер не подтвердил завершение запуска: процесс или файл результата отсутствует. Это может быть перезагрузка или нехватка памяти. Повторный деплой автоматически не запускается.");
                    if (cursor >= size) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (SshAuthenticationException) {
                    if (launched) throw new RemoteOutcomeUnknownException("SSH больше не принимает учётные данные. Результат уже начатой команды неизвестен; проверьте сервер.");
                    throw AuthenticationError(project);
                }
                catch (Exception e) when (e is SshConnectionException or SshOperationTimeoutException or SocketException or IOException) {
                    client?.Dispose(); client = null;
                    await Disconnected();
                    if (disconnectedAt is { } lostAt && DateTimeOffset.UtcNow - lostAt > TimeSpan.FromMinutes(2))
                        throw new RemoteOutcomeUnknownException("SSH недоступен больше 2 минут. Удалённая команда может продолжаться; результат не подтверждён. Проверьте сервер перед новым запуском.");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
            throw new RemoteOutcomeUnknownException("Истекло время проверки результата на сервере. Удалённый таймаут ограничивает саму команду; проверьте журнал перед новым запуском.");
        }
        finally { client?.Dispose(); }
    }
}
