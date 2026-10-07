using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;

namespace DeployIt.Services;

public sealed partial class SshDeploymentService
{
    private static string RunPrelude(DeploymentProject project, Guid runId, string? credentialsDirectory = null) => $$"""
        set -euo pipefail
        umask 077
        phase=prepare
        printf 'DEPLOYIT_STAGE=%s\n' "$phase"
        root={{Quote(project.WorkingDirectory)}}
        pid_file="$root/.deployit-process-{{runId:N}}"
        cancel_file="$root/.deployit-stop-{{runId:N}}"
        cleanup() {
            result=$?
            trap - EXIT
            if [ "$result" -ne 0 ]; then printf 'DEPLOYIT_FAILURE=%s:%s\n' "$phase" "$result" >&2; fi
            rm -f -- "$pid_file"
            {{(credentialsDirectory is null ? ":" : "rm -rf -- " + Quote(credentialsDirectory))}}
            exit "$result"
        }
        trap cleanup EXIT
        check_cancel() { if [ -f "$cancel_file" ]; then exit 130; fi; }
        mkdir -p -- "$root"
        # GNU timeout is the process-group leader and the direct parent of this shell.
        printf '%s\n' "$PPID" > "$pid_file"
        check_cancel
        """;

    public async Task StopRunAsync(DeploymentProject project, Guid runId, CancellationToken ct)
    {
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        Pin(client, project.HostFingerprint);
        await client.ConnectAsync(ct);
        var script = $$"""
            set -euo pipefail
            umask 077
            root={{Quote(project.WorkingDirectory)}}
            run_id={{Quote(runId.ToString("N"))}}
            mkdir -p -- "$root"
            # A stop requested before process startup also prevents any deployment work.
            : > "$root/.deployit-stop-$run_id"
            pid_file="$root/.deployit-process-$run_id"
            if [ ! -f "$pid_file" ]; then
                exec 9>"$root/.deployit.lock"
                flock -n 9 || exit 84
                exit 0
            fi
            read -r pid < "$pid_file"
            case "$pid" in ''|*[!0-9]*) exit 83 ;; esac
            [ "$pid" -gt 1 ] || exit 83
            if [ ! -r "/proc/$pid/cmdline" ]; then exit 83; fi
            cmdline=$(tr '\0' ' ' < "/proc/$pid/cmdline")
            # Never signal a reused/unrelated PID from a stale file.
            case "$cmdline" in *"timeout --signal=TERM"*"$run_id"*) ;; *) exit 83 ;; esac
            kill -TERM -- "-$pid" 2>/dev/null || true
            for ((attempt=0; attempt<50; attempt++)); do
                if ! kill -0 -- "-$pid" 2>/dev/null; then exit 0; fi
                sleep 0.1
            done
            kill -KILL -- "-$pid" 2>/dev/null || true
            for ((attempt=0; attempt<20; attempt++)); do
                if ! kill -0 -- "-$pid" 2>/dev/null; then exit 0; fi
                sleep 0.1
            done
            exit 82
            """;
        using var command = client.CreateCommand("bash --noprofile --norc -c " + Quote(script));
        command.CommandTimeout = TimeSpan.FromSeconds(12);
        await command.ExecuteAsync(ct);
        if (command.ExitStatus != 0)
            throw new DomainException("Сервер не подтвердил остановку группы процессов. Проверьте команду в SSH-терминале перед новым запуском. Например: ps -eo pid,pgid,args | grep '[t]imeout'.");
    }
}
