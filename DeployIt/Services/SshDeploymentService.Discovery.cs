using System.Text;
using System.Text.Json;
using DeployIt.DTOs;
using DeployIt.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DeployIt.Services;

public sealed partial class SshDeploymentService : IProjectDiscovery
{
    public async Task<DiscoveryScan> DiscoverAsync(DeploymentProject target, string searchDirectory, CancellationToken ct)
    {
        using var key = LoadKey(target);
        using var client = new SshClient(Connection(target, key));
        Pin(client, target.HostFingerprint);
        try {
            await client.ConnectAsync(ct);
            using var command = client.CreateCommand(BuildDiscoveryCommand(searchDirectory));
            command.CommandTimeout = TimeSpan.FromSeconds(75);
            await command.ExecuteAsync(ct);
            if (command.ExitStatus != 0)
                throw new DomainException("Поиск не завершён. Проверьте каталог, права SSH-пользователя и наличие bash, find, git, base64 и timeout. Проекты не добавлены.");
            return ParseDiscovery(command.Result);
        }
        catch (DomainException) { throw; }
        catch (SshAuthenticationException) { throw AuthenticationError(target); }
        catch (Exception) when (!ct.IsCancellationRequested) {
            throw new DomainException("Не удалось прочитать проекты по SSH. Проверьте подключение и отпечаток сервера. Поиск можно повторить.");
        }
    }

    public static string BuildDiscoveryCommand(string searchDirectory)
    {
        if (!searchDirectory.StartsWith('/') || searchDirectory.Any(char.IsControl)
            || searchDirectory.Split('/').Any(p => p is "." or ".."))
            throw new DomainException("Укажите абсолютный каталог поиска, например /opt.");
        const string dockerFormat = "{{index .Config.Labels \"com.docker.compose.project\"}}\t{{index .Config.Labels \"com.docker.compose.project.working_dir\"}}";
        var script = $$"""
            set -euo pipefail
            export GIT_OPTIONAL_LOCKS=0 GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
            base={{Quote(searchDirectory)}}
            [ -d "$base" ] && [ -r "$base" ] && [ -x "$base" ] || exit 66
            base=$(readlink -f -- "$base")
            encode() { printf '%s' "$1" | base64 -w0; }
            warn() { printf 'W\t%s\n' "$(encode "$1")"; }
            # Inspect only labels, never container environment variables or volume contents.
            docker_rows=
            if command -v docker >/dev/null; then
                if ids=$(docker ps -aq 2>/dev/null); then
                    if [ -n "$ids" ]; then
                        docker_rows=$(docker inspect --format {{Quote(dockerFormat)}} $ids 2>/dev/null) || warn 'Не удалось прочитать Docker-метки. Старые проекты без маркера могут быть пропущены.'
                    fi
                else warn 'Docker недоступен этому пользователю. Старые проекты без маркера могут быть пропущены.'; fi
            fi
            if ! paths=$(find "$base" -maxdepth 8 \( -type d \( -name releases -o -name .git -o -name node_modules \) -prune \) -o \( -type f -name .deployit-project -o -type l -name current \) -print0 2>/dev/null | base64 -w0); then
                warn 'Часть каталогов недоступна. Результат поиска может быть неполным.'
            fi
            declare -A seen
            count=0
            while IFS= read -r -d '' candidate; do
                root=${candidate%/*}
                [ -z "${seen[$root]+yes}" ] || continue
                seen[$root]=1
                count=$((count + 1))
                if [ "$count" -gt 200 ]; then warn 'Достигнут предел 200 каталогов. Повторите поиск в более узком каталоге.'; break; fi
                [ "$(readlink -f -- "$root")" = "$root" ] || { warn "Пропущен каталог с символической ссылкой: $root"; continue; }
                id=
                if [ -f "$root/.deployit-project" ] && [ ! -L "$root/.deployit-project" ]; then
                    id=$(head -c 64 -- "$root/.deployit-project")
                    [[ "$id" =~ ^[a-f0-9]{32}$ ]] || { warn "Некорректный маркер: $root"; continue; }
                fi
                release=
                if [ -e "$root/current" ] || [ -L "$root/current" ]; then
                    [ -L "$root/current" ] || { warn "current должен быть ссылкой: $root"; continue; }
                    release=$(readlink -f -- "$root/current") || { warn "Не найден текущий релиз: $root"; continue; }
                    release_id=${release##*/}
                    [[ "$release_id" =~ ^[a-f0-9]{32}$ ]] && [ "$release" = "$root/releases/$release_id" ] && [ -d "$release" ] || { warn "Небезопасный путь текущего релиза: $root"; continue; }
                fi
                conflict=false
                while IFS=$'\t' read -r compose working; do
                    [[ "$compose" =~ ^deployit-([a-f0-9]{32})$ ]] || continue
                    docker_id=${BASH_REMATCH[1]}
                    [ -n "$release" ] && [ "$(readlink -f -- "$working" 2>/dev/null || true)" = "$release" ] || continue
                    if [ -n "$id" ] && [ "$id" != "$docker_id" ]; then conflict=true; break; fi
                    id=$docker_id
                done <<< "$docker_rows"
                [ "$conflict" = false ] || { warn "Конфликт ID проекта и Docker-меток: $root"; continue; }
                [ -n "$id" ] || { warn "Не удалось определить прежний ID проекта: $root"; continue; }
                metadata=
                if [ -f "$root/.deployit-project.json" ] && [ ! -L "$root/.deployit-project.json" ]; then
                    metadata=$(head -c 65537 -- "$root/.deployit-project.json")
                fi
                url=; branch=
                if [ -n "$release" ] && [ -d "$release/.git" ] && [ ! -L "$release/.git" ]; then
                    url=$(git -C "$release" config --local --get remote.origin.url || true)
                    branch=$(git -C "$release" symbolic-ref --short -q HEAD || true)
                fi
                printf 'P\t%s\t%s\t%s\t%s\t%s\n' "$id" "$(encode "$root")" "$(encode "$url")" "$(encode "$branch")" "$(encode "$metadata")"
            done < <(printf '%s' "$paths" | base64 -d)
            """;
        // This command performs no writes, creates no locks and executes no project code.
        return $"timeout --signal=TERM --kill-after=5s 60s bash --noprofile --norc -c {Quote(script)}";
    }

    public static DiscoveryScan ParseDiscovery(string output)
    {
        if (output.Length > 20_000_000) throw new DomainException("Слишком большой результат поиска. Укажите более узкий каталог.");
        var projects = new List<DiscoveredProject>();
        var warnings = new List<string>();
        static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields[0] == "W" && fields.Length == 2) { warnings.Add(Decode(fields[1])); continue; }
            if (fields.Length != 6 || fields[0] != "P" || !Guid.TryParseExact(fields[1], "N", out var id) || id == Guid.Empty)
                throw new DomainException("Сервер вернул некорректный результат поиска. Проекты не добавлены.");
            var path = Decode(fields[2]);
            try { ValidateStoragePath(path); }
            catch (DomainException) { warnings.Add($"Пропущен небезопасный каталог: {path}"); continue; }
            RecoveryMetadata? metadata = null;
            var json = Decode(fields[5]);
            if (json.Length > 0) {
                try {
                    if (json.Length > 65536) throw new JsonException();
                    metadata = JsonSerializer.Deserialize<RecoveryMetadata>(json);
                    if (metadata is null || metadata.Version != 1 || metadata.Id != id) throw new JsonException();
                }
                catch (JsonException) { warnings.Add($"Не удалось прочитать настройки: {path}. Использованы данные Git."); metadata = null; }
            }
            var url = Decode(fields[3]); var branch = Decode(fields[4]);
            // A stale manifest must not override the repository actually checked out on the server.
            if (metadata is not null && url.Length > 0 && metadata.RepositoryUrl != url) {
                warnings.Add($"Настройки и Git-репозиторий различаются: {path}. Использованы данные Git."); metadata = null;
            }
            projects.Add(new(id, path, url.Length > 0 ? url : metadata?.RepositoryUrl ?? "",
                branch.Length > 0 ? branch : metadata?.Branch ?? "main", metadata));
        }
        return new(projects, warnings);
    }
}
