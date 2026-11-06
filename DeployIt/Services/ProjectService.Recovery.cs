using System.Text;
using System.Text.RegularExpressions;
using DeployIt.DTOs;
using DeployIt.Models;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace DeployIt.Services;

public sealed partial class ProjectService
{
    private static void RequireReviewed(DeploymentProject project)
    {
        if (project.RequiresReview)
            throw new DomainException("Проект восстановлен с сервера. Сначала проверьте Git-подключение, каталог и команды в настройках и сохраните их. Сервер пока не изменён.", 409);
    }

    public async Task<RecoveryResult> RecoverProjectsAsync(RecoveryInput input, IProjectDiscovery discovery, CancellationToken ct = default)
    {
        Validate(input);
        ValidateServer(input.Host, input.Port);
        if (!Regex.IsMatch(input.Username, "^[a-zA-Z_][a-zA-Z0-9_.-]{0,99}$")
            || !Regex.IsMatch(input.HostFingerprint.Trim(), "^SHA256:[A-Za-z0-9+/]{43}$"))
            throw new DomainException("Укажите SSH-пользователя и проверенный отпечаток сервера SHA256:…");
        var target = new DeploymentProject { Host = input.Host.ToLowerInvariant(), Port = input.Port,
            Username = input.Username, HostFingerprint = input.HostFingerprint.Trim(), AuthenticationType = input.AuthenticationType };
        switch (input.AuthenticationType) {
            case SshAuthenticationType.Password:
                if (string.IsNullOrEmpty(input.Password)) throw new DomainException("Укажите пароль SSH-пользователя.");
                target.ProtectedPassword = secrets.Protect(input.Password);
                break;
            case SshAuthenticationType.PrivateKey:
                try { using var key = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(input.PrivateKey)),
                    string.IsNullOrEmpty(input.Passphrase) ? null : input.Passphrase); }
                catch (Exception) { throw new DomainException("Не удалось прочитать приватный SSH-ключ. Вставьте ключ целиком и укажите его пароль, если он установлен."); }
                target.ProtectedPrivateKey = secrets.Protect(input.PrivateKey);
                target.ProtectedPassphrase = secrets.Protect(input.Passphrase);
                break;
            default: throw new DomainException("Выберите способ входа по SSH.");
        }
        var scan = await discovery.DiscoverAsync(target, input.SearchDirectory, ct);
        await mutationGate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var existing = await db.Projects.AsNoTracking().ToListAsync(ct);
            var connections = await db.Connections.ToListAsync(ct);
            var results = new List<RecoveredProjectView>();
            foreach (var found in scan.Projects) {
                var sameId = existing.FirstOrDefault(p => p.Id == found.Id);
                if (sameId is not null) {
                    var sameLocation = sameId.Host == target.Host && sameId.Port == target.Port && sameId.WorkingDirectory == found.WorkingDirectory;
                    results.Add(new(sameLocation ? sameId.Id : null, found.WorkingDirectory,
                        sameLocation
                            ? "Уже подключён. Настройки сохранены без изменений." : "Пропущен: этот ID уже используется в другом месте.", false));
                    continue;
                }
                if (scan.Projects.Count(p => p.Id == found.Id) > 1
                    || scan.Projects.Any(p => p != found && DirectoriesOverlap(p.WorkingDirectory, found.WorkingDirectory))
                    || existing.Any(p => p.Host == target.Host && p.Port == target.Port && DirectoriesOverlap(p.WorkingDirectory, found.WorkingDirectory))) {
                    results.Add(new(null, found.WorkingDirectory, "Пропущен: конфликт ID или каталогов проектов.", false)); continue;
                }
                if (found.Id == Guid.Empty || found.WorkingDirectory.Length > 500
                    || !Uri.TryCreate(found.RepositoryUrl, UriKind.Absolute, out var repository)
                    || repository.Scheme != "https" || repository.UserInfo != "" || repository.Query != "" || repository.Fragment != ""
                    || repository.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2) {
                    results.Add(new(null, found.WorkingDirectory, "Пропущен: не удалось определить безопасный HTTPS-адрес Git-репозитория.", false)); continue;
                }
                var url = repository.AbsoluteUri;
                if (existing.Any(p => p.RepositoryUrl == url)) {
                    results.Add(new(null, found.WorkingDirectory, "Пропущен: репозиторий уже подключён к другому проекту.", false)); continue;
                }
                var provider = repository.Host == "github.com" ? GitProvider.GitHub : GitProvider.GitLab;
                var baseUrl = GitProviderService.NormalizeBaseUrl(provider, repository.GetLeftPart(UriPartial.Authority));
                var name = repository.AbsolutePath.Trim('/');
                if (name.EndsWith(".git", StringComparison.Ordinal)) name = name[..^4];
                var owner = name.Split('/')[0];
                var connection = connections.FirstOrDefault(c => c.BaseUrl == baseUrl && c.PublicOnly && c.Account == owner);
                if (connection is null) {
                    connection = new GitConnection { Name = Truncate("Восстановлено: " + owner),
                        Provider = provider, BaseUrl = baseUrl, PublicOnly = true, Account = owner };
                    db.Connections.Add(connection); connections.Add(connection);
                }
                var metadata = found.Metadata;
                var project = new DeploymentProject { Id = found.Id, ConnectionId = connection.Id,
                    Host = target.Host, Port = target.Port, Username = target.Username, HostFingerprint = target.HostFingerprint,
                    AuthenticationType = target.AuthenticationType, ProtectedPassword = target.ProtectedPassword,
                    ProtectedPrivateKey = target.ProtectedPrivateKey, ProtectedPassphrase = target.ProtectedPassphrase,
                    WorkingDirectory = found.WorkingDirectory, RepositoryUrl = url, RepositoryName = name,
                    RepositoryId = metadata?.RepositoryId ?? "", Branch = found.Branch,
                    Name = metadata?.Name is { Length: > 0 and <= 100 } title ? title : Truncate(name.Split('/').Last()),
                    RequiresReview = true };
                if (metadata is not null && metadata.Id == found.Id && metadata.Version == 1) {
                    if (ValidCommand(metadata.DeployCommand)) project.DeployCommand = metadata.DeployCommand;
                    if (ValidCommand(metadata.RestartCommand)) project.RestartCommand = metadata.RestartCommand;
                    if (ValidCommand(metadata.StopCommand)) project.StopCommand = metadata.StopCommand;
                    if (metadata.TimeoutMinutes is >= 1 and <= 120) project.TimeoutMinutes = metadata.TimeoutMinutes;
                }
                db.Projects.Add(project); existing.Add(project);
                results.Add(new(project.Id, project.WorkingDirectory, "Подключён с прежним ID. Проверьте настройки перед первым запуском.", true));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(results, scan.Warnings);
        }
        finally { mutationGate.Release(); }
    }

    private static string Truncate(string value) => value[..Math.Min(100, value.Length)];
    private static bool ValidCommand(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 10000 && !value.Contains('\0');
    private static bool DirectoriesOverlap(string left, string right)
    {
        static string Normalize(string path) => "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        left = Normalize(left); right = Normalize(right);
        return left == right || left.StartsWith(right + "/", StringComparison.Ordinal) || right.StartsWith(left + "/", StringComparison.Ordinal);
    }
}
