using DeployIt.DTOs;
using System.Text.Json;
using DeployIt.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeployIt.Services;

public sealed partial class SshDeploymentService
{
    public const int ReleasesToKeep = 3;

    public async Task<List<ReleaseView>> ReleasesAsync(DeploymentProject project, CancellationToken ct)
    {
        var result = await RunStorageCommand(project, BuildReleaseCommand(project, [], null), ct);
        var rows = JsonSerializer.Deserialize<List<StoredRelease>>(result, JsonSerializerOptions.Web)
            ?? throw new DomainException("Сервер не вернул список релизов.");
        return rows.Select(r => new ReleaseView(Guid.ParseExact(r.Id, "N"), DateTimeOffset.FromUnixTimeSeconds(r.ModifiedAtUnix),
            r.SizeBytes, r.IsCurrent, r.InUse)).OrderByDescending(r => r.IsCurrent).ThenByDescending(r => r.ModifiedAt).ToList();
    }

    public Task DeleteReleaseAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, Guid releaseId, CancellationToken ct)
        => RunStorageCommand(project, BuildReleaseCommand(project, knownReleases, releaseId), ct);

    private sealed record StoredRelease(string Id, long ModifiedAtUnix, long SizeBytes, bool IsCurrent, bool InUse);

    public Task CleanReleasesAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, CancellationToken ct)
        => RunStorageCommand(project, BuildStorageCommand(project, knownReleases, false), ct);

    public Task DeleteAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, CancellationToken ct)
        => RunStorageCommand(project, BuildStorageCommand(project, knownReleases, true), ct);

    private async Task<string> RunStorageCommand(DeploymentProject project, string script, CancellationToken ct)
    {
        using var key = LoadKey(project);
        using var client = new SshClient(Connection(project, key));
        Pin(client, project.HostFingerprint);
        try
        {
            await client.ConnectAsync(ct);
            using var command = client.CreateCommand(script);
            command.CommandTimeout = TimeSpan.FromMinutes(project.TimeoutMinutes) + TimeSpan.FromSeconds(15);
            await command.ExecuteAsync(ct);
            if (command.ExitStatus != 0)
                throw new DomainException(command.ExitStatus switch {
                    75 => "Каталог занят другой операцией. Дождитесь её завершения и повторите удаление или очистку.",
                    76 => "Очистка отменена: каталог, releases или current ссылается за пределы проекта. Проверьте пути на сервере.",
                    77 => "Очистка отменена: принадлежность каталога этому проекту не подтверждена. Нужен маркер .deployit-project или релиз из истории этого проекта.",
                    78 => "Не удалось проверить или удалить Docker-ресурсы проекта. Проверьте Docker и права SSH-пользователя.",
                    79 => "Текущий релиз удалить нельзя. Сначала разверните другую версию.",
                    80 => "Релиз подключён к Docker-контейнеру. Освободите его перед удалением.",
                    81 => "Релиз не найден. Обновите список релизов.",
                    _ => "Очистка сервера не завершена. Проверьте права, свободное место и команду остановки. Запись проекта сохранена; повторите операцию."
                }, command.ExitStatus == 81 ? 404 : 409);
            return command.Result;
        }
        catch (DomainException) { throw; }
        catch (SshAuthenticationException) { throw AuthenticationError(project); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            throw new DomainException("Не удалось подтвердить очистку сервера по SSH. Запись проекта сохранена. Проверьте сервер и повторите операцию.", 409);
        }
    }

    private static string ReleaseUsageFunctions(DeploymentProject project) => $$"""
        load_release_usage() {
            mounted_paths=
            if command -v docker >/dev/null; then
                container_ids=$(docker ps -aq --filter {{Quote("label=com.docker.compose.project=deployit-" + project.Id.ToString("N"))}}) || return 1
                if [ -n "$container_ids" ]; then
                    mounted_paths=$(docker inspect --format {{Quote("{{range .Mounts}}{{println .Source}}{{end}}")}} $container_ids) || return 1
                fi
            fi
        }
        release_in_use() {
            local source
            while IFS= read -r source; do
                case "$source" in "$1"|"$1"/*) return 0 ;; esac
            done <<< "$mounted_paths"
            return 1
        }
        """;

    private static string StorageOwnershipCheck(IReadOnlyCollection<Guid> knownReleases) => $$"""
        owned=false
        if [ -f "$root/.deployit-project" ]; then owned=true; fi
        for release_id in {{string.Join(' ', knownReleases.Select(id => Quote(id.ToString("N"))))}}; do
            if [ -d "$root/releases/$release_id" ] && [ ! -L "$root/releases/$release_id" ]; then owned=true; fi
        done
        if [ -z "$(find "$root" -mindepth 1 -maxdepth 1 ! -name .deployit.lock -print -quit)" ]; then owned=true; fi
        [ "$owned" = true ] || exit 77
        """;

    public static string BuildReleaseCommand(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, Guid? releaseId)
    {
        ValidateStoragePath(project.WorkingDirectory);
        var action = releaseId.HasValue ? $$"""
            {{StorageOwnershipCheck(knownReleases)}}
            candidate="$root/releases/{{releaseId.Value:N}}"
            [ -d "$candidate" ] && [ ! -L "$candidate" ] || exit 81
            [ "$candidate" != "$current_path" ] || exit 79
            if release_in_use "$candidate"; then exit 80; fi
            rm -rf --one-file-system -- "$candidate"
            """ : """
            printf '['
            separator=
            shopt -s nullglob
            for directory in "$root/releases/"*; do
                [ -d "$directory" ] && [ ! -L "$directory" ] || continue
                name=${directory##*/}
                [[ "$name" =~ ^[a-f0-9]{32}$ ]] || continue
                modified=$(stat -c '%Y' -- "$directory")
                size=$(du -sx --block-size=1 -- "$directory" | cut -f1)
                current=false; in_use=false
                if [ "$directory" = "$current_path" ]; then current=true; fi
                if release_in_use "$directory"; then in_use=true; fi
                printf '%s{"id":"%s","modifiedAtUnix":%s,"sizeBytes":%s,"isCurrent":%s,"inUse":%s}' "$separator" "$name" "$modified" "$size" "$current" "$in_use"
                separator=,
            done
            printf ']'
            """;
        var script = $$"""
            set -euo pipefail
            root={{Quote(project.WorkingDirectory)}}
            if [ ! -e "$root" ] && [ ! -L "$root" ]; then
                {{(releaseId.HasValue ? "exit 81" : "printf '[]'; exit 0")}}
            fi
            {{CheckStorageRoot(project)}}
            umask 077
            exec 9>"$root/.deployit.lock"
            flock -n 9 || exit 75
            current_path=
            if [ -L "$root/current" ]; then current_path=$(readlink -m -- "$root/current"); fi
            {{ReleaseUsageFunctions(project)}}
            load_release_usage || exit 78
            {{action}}
            """;
        var seconds = releaseId.HasValue ? project.TimeoutMinutes * 60 : 60;
        return $"timeout --signal=TERM --kill-after=15s {seconds}s bash --noprofile --norc -c {Quote(script)}";
    }

    // A destructive operation must never accept a system directory or follow an alias.
    private static void ValidateStoragePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!path.StartsWith('/') || path.EndsWith('/') || parts.Length < 2
            || parts.Any(p => p is "." or "..") || path.Any(char.IsControl)
            || parts[0] is "bin" or "boot" or "dev" or "etc" or "lib" or "lib64" or "proc" or "run" or "sbin" or "sys" or "usr"
            || (parts[0] is "home" or "root" && parts.Length < 3)
            || path is "/var/lib" or "/var/www" or "/var/log" or "/var/cache"
            || path == "/var/lib/docker" || path.StartsWith("/var/lib/docker/")
            || path == "/var/lib/containerd" || path.StartsWith("/var/lib/containerd/"))
            throw new DomainException("Для очистки нужен отдельный каталог приложения, например /opt/apps/my-app. Системные каталоги удалять нельзя.");
    }

    private static string CheckStorageRoot(DeploymentProject project) => $$"""
        root={{Quote(project.WorkingDirectory)}}
        [ ! -L "$root" ] && [ -d "$root" ] || exit 76
        [ "$(readlink -f -- "$root")" = "$root" ] || exit 76
        [ ! -L "$root/releases" ] || exit 76
        if [ -e "$root/releases" ]; then [ -d "$root/releases" ] || exit 76; fi
        if [ -e "$root/current" ] || [ -L "$root/current" ]; then
            [ -L "$root/current" ] || exit 76
            current_path=$(readlink -m -- "$root/current")
            current_id=${current_path##*/}
            [[ "$current_id" =~ ^[a-f0-9]{32}$ ]] && [ "$current_path" = "$root/releases/$current_id" ] || exit 76
        fi
        if [ -e "$root/.deployit-project" ] || [ -L "$root/.deployit-project" ]; then
            [ ! -L "$root/.deployit-project" ] && [ -f "$root/.deployit-project" ] || exit 77
            [ "$(cat -- "$root/.deployit-project")" = {{Quote(project.Id.ToString("N"))}} ] || exit 77
        fi
        """;

    private static string MarkStorageRoot(DeploymentProject project) => $$"""
        {{CheckStorageRoot(project)}}
        if [ ! -e "$root/.deployit-project" ]; then
            (umask 077; set -o noclobber; printf '%s\n' {{Quote(project.Id.ToString("N"))}} > "$root/.deployit-project")
        fi
        """;

    // Runs while holding .deployit.lock, including from the deployment's EXIT trap.
    // Keep current and newest other releases. Bind-mounted releases remain protected.
    private static string ReleaseCleanupFunction(DeploymentProject project) => $$"""
        cleanup_releases() (
            set -euo pipefail
            {{CheckStorageRoot(project)}}
            [ -d "$root/releases" ] || return 0
            current_path=
            if [ -L "$root/current" ]; then current_path=$(readlink -m -- "$root/current"); fi
            {{ReleaseUsageFunctions(project)}}
            load_release_usage || return 1
            kept=0
            if [ -n "$current_path" ] && [ -d "$current_path" ]; then kept=1; fi
            # Sort only directory names emitted by find; never parse arbitrary shell paths.
            candidates=$(find "$root/releases" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %f\n' | sort -rn) || return 1
            while read -r modified name; do
                [[ "$name" =~ ^[a-f0-9]{32}$ ]] || continue
                candidate="$root/releases/$name"
                [ "$candidate" != "$current_path" ] || continue
                if [ "$kept" -lt {{ReleasesToKeep}} ]; then kept=$((kept + 1)); continue; fi
                if release_in_use "$candidate"; then continue; fi
                rm -rf --one-file-system -- "$candidate" || return 1
                printf 'Удалён старый релиз: %s\n' "$name"
            done <<< "$candidates"
        )
        """;

    public static string BuildStorageCommand(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, bool delete)
    {
        ValidateStoragePath(project.WorkingDirectory);
        var requiresDocker = knownReleases.Count > 0 && project.DeployCommand.Contains("docker", StringComparison.OrdinalIgnoreCase);
        var script = $$"""
            set -euo pipefail
            umask 077
            root={{Quote(project.WorkingDirectory)}}
            if [ -e "$root" ] || [ -L "$root" ]; then
                {{CheckStorageRoot(project)}}
                exec 9>"$root/.deployit.lock"
                flock -n 9 || exit 75
                {{StorageOwnershipCheck(knownReleases)}}
                {{(delete ? DeleteProjectFiles(project, requiresDocker) : ReleaseCleanupFunction(project) + "\ncleanup_releases")}}
            {{(delete ? "else\n" + DeleteDockerResources(project, requiresDocker) : "")}}
            fi
            """;
        return $"timeout --signal=TERM --kill-after=15s {project.TimeoutMinutes * 60}s bash --noprofile --norc -c {Quote(script)}";
    }

    private static string DeleteProjectFiles(DeploymentProject project, bool requiresDocker) => $$"""
        export DEPLOYIT_PROJECT_DIR="$root"
        export DEPLOYIT_ENV_FILE="$root/.env"
        export COMPOSE_PROJECT_NAME=deployit-{{project.Id:N}}
        if [ -d "$root/current" ]; then
            export DEPLOYIT_RELEASE_DIR=$(readlink -f -- "$root/current")
            # Compose resources are removed below by their project label, even if YAML is broken.
            {{(project.StopCommand == "docker compose stop" ? ":" : "(cd -- \"$root/current\"; bash -lc " + Quote(project.StopCommand) + ")")}}
        fi
        {{DeleteDockerResources(project, requiresDocker)}}
        cd /
        rm -rf --one-file-system -- "$root"
        """;

    private static string DeleteDockerResources(DeploymentProject project, bool requiresDocker) => $$"""
        if command -v docker >/dev/null; then
            label={{Quote("label=com.docker.compose.project=deployit-" + project.Id.ToString("N"))}}
            # Never prune global images, caches, external volumes or other applications.
            ids=$(docker ps -aq --filter "$label") || exit 78
            if [ -n "$ids" ]; then docker rm -fv $ids || exit 78; fi
            ids=$(docker network ls -q --filter "$label") || exit 78
            if [ -n "$ids" ]; then docker network rm $ids || exit 78; fi
            ids=$(docker volume ls -q --filter "$label") || exit 78
            if [ -n "$ids" ]; then docker volume rm $ids || exit 78; fi
        {{(requiresDocker ? "else\nexit 78" : "")}}
        fi
        """;
}
