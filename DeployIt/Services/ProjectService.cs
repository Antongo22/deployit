using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeployIt.Data;
using DeployIt.DTOs;
using DeployIt.Models;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;

namespace DeployIt.Services;

public sealed class ProjectService(IDbContextFactory<DeployItDbContext> factory,
    GitProviderService git, SecretProtector secrets)
{
    public async Task<List<ConnectionView>> ConnectionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(View).ToList();
    }

    public async Task<ConnectionView> AddConnectionAsync(ConnectionInput input, CancellationToken ct = default)
    {
        Validate(input);
        if (!input.PublicOnly && string.IsNullOrWhiteSpace(input.Token))
            throw new DomainException("Заполните поле «Access token» токеном из настроек Git-сервиса. Если нужен только публичный репозиторий, включите «Только публичные репозитории, без токена» и укажите имя аккаунта. Пример имени: octocat.");
        var connection = new GitConnection { Name = input.Name.Trim(), Provider = input.Provider,
            BaseUrl = GitProviderService.NormalizeBaseUrl(input.Provider, input.BaseUrl),
            PublicOnly = input.PublicOnly, Account = input.Account.Trim(),
            ProtectedToken = input.PublicOnly ? "" : secrets.Protect(input.Token.Trim()) };
        var identity = await git.InspectAsync(connection, ct);
        connection.Account = identity.Account;
        connection.ExternalUserId = identity.UserId;
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Connections.Add(connection);
        await db.SaveChangesAsync(ct);
        return View(connection);
    }

    public async Task DeleteConnectionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Projects.AnyAsync(p => p.ConnectionId == id, ct))
            throw new DomainException("Сначала удалите проекты этого подключения.", 409);
        var connection = await db.Connections.FindAsync([id], ct) ?? throw new DomainException("Подключение не найдено.", 404);
        db.Remove(connection);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateTokenAsync(Guid id, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1000)
            throw new DomainException("Укажите новый токен.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = await db.Connections.FindAsync([id], ct) ?? throw new DomainException("Подключение не найдено.", 404);
        if (connection.PublicOnly) throw new DomainException("Публичное подключение не использует токен.");
        connection.ProtectedToken = secrets.Protect(token.Trim());
        var identity = await git.InspectAsync(connection, ct);
        if (identity.UserId != connection.ExternalUserId)
            throw new DomainException("Новый токен должен принадлежать тому же аккаунту.");
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<RepositoryView>> RepositoriesAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new DomainException("Подключение не найдено.", 404);
        return await git.RepositoriesAsync(connection, ct);
    }

    public async Task<List<ProjectView>> ProjectsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var runs = await db.Deployments.AsNoTracking().OrderByDescending(d => d.CreatedAt)
            .Select(d => new DeploymentView(d.Id, d.ProjectId, d.Status, d.CreatedAt, d.StartedAt, d.FinishedAt,
                d.CommitSha, d.ExitCode, null)).ToListAsync(ct);
        return projects.Select(p => View(p, runs.FirstOrDefault(d => d.ProjectId == p.Id), runs.FirstOrDefault(d => d.ProjectId == p.Id && d.Status == DeploymentStatus.Succeeded))).ToList();
    }

    public async Task<ProjectView> ProjectAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new DomainException("Проект не найден.", 404);
        var latest = await db.Deployments.AsNoTracking().Where(d => d.ProjectId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DeploymentView(d.Id, d.ProjectId, d.Status, d.CreatedAt, d.StartedAt, d.FinishedAt,
                d.CommitSha, d.ExitCode, null)).FirstOrDefaultAsync(ct);
        var successful = await db.Deployments.AsNoTracking().Where(d => d.ProjectId == id && d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DeploymentView(d.Id, d.ProjectId, d.Status, d.CreatedAt, d.StartedAt, d.FinishedAt,
                d.CommitSha, d.ExitCode, null)).FirstOrDefaultAsync(ct);
        return View(project, latest, successful);
    }

    private readonly SemaphoreSlim mutationGate = new(1, 1);
    public async Task<ProjectView> SaveProjectAsync(ProjectInput input, Guid? id = null, CancellationToken ct = default)
    {
        await mutationGate.WaitAsync(ct);
        try { return await SaveProjectCoreAsync(input, id, ct); }
        finally { mutationGate.Release(); }
    }
    private async Task<ProjectView> SaveProjectCoreAsync(ProjectInput input, Guid? id, CancellationToken ct)
    {
        Validate(input);
        ValidateServer(input.Host, input.Port);
        if (!Enum.IsDefined(input.AuthenticationType))
            throw new DomainException("Выберите способ входа по SSH. Например: «Логин и пароль» для входа с паролем пользователя сервера.");
        if (!Regex.IsMatch(input.Username, "^[a-zA-Z_][a-zA-Z0-9_.-]{0,99}$"))
            throw new DomainException("Некорректное имя SSH-пользователя.");
        if (!Regex.IsMatch(input.HostFingerprint.Trim(), "^SHA256:[A-Za-z0-9+/]{43}$"))
            throw new DomainException("Укажите отпечаток сервера в формате SHA256:…");
        if (!Regex.IsMatch(input.Branch, "^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")
            || input.Branch.Contains("..") || input.Branch.Contains("//") || input.Branch.Contains("@{")
            || input.Branch.EndsWith('/') || input.Branch.Split('/').Any(s => s.StartsWith('.') || s.EndsWith('.') || s.EndsWith(".lock")))
            throw new DomainException("Некорректное имя ветки Git.");
        if (!input.WorkingDirectory.StartsWith('/') || input.WorkingDirectory == "/"
            || input.WorkingDirectory.Any(char.IsControl) || input.WorkingDirectory.Split('/').Any(s => s is "." or ".."))
            throw new DomainException("Укажите абсолютный отдельный каталог проекта, например /opt/apps/my-app.");
        if (string.IsNullOrWhiteSpace(input.DeployCommand) || input.DeployCommand.Contains('\0'))
            throw new DomainException("Укажите команду деплоя.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var project = id.HasValue
            ? await db.Projects.FindAsync([id.Value], ct) ?? throw new DomainException("Проект не найден.", 404)
            : new DeploymentProject();
        if (id.HasValue && await db.Deployments.AnyAsync(d => d.ProjectId == id &&
            (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Running), ct))
            throw new DomainException("Дождитесь завершения деплоя перед редактированием.", 409);
        var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.ConnectionId, ct)
            ?? throw new DomainException("Выберите Git-подключение.");
        var repository = await git.RepositoryAsync(connection, input.RepositoryId, ct);
        var host = input.Host.Trim().ToLowerInvariant();
        if (await db.Projects.AnyAsync(p => p.Id != project.Id && (p.RepositoryUrl == repository.CloneUrl
            || (p.Host == host && p.Port == input.Port)), ct))
            throw new DomainException("Репозиторий или сервер уже привязан к другому проекту (связь 1:1).", 409);
        if (input.AuthenticationType == SshAuthenticationType.Password)
        {
            if (!string.IsNullOrEmpty(input.Password))
                project.ProtectedPassword = secrets.Protect(input.Password);
            else if (project.AuthenticationType != SshAuthenticationType.Password || project.ProtectedPassword == "")
                throw new DomainException("Укажите пароль SSH-пользователя на сервере. Пример: для ssh deploy@app.example.com нужны логин deploy и пароль этого пользователя, а не пароль SSH-ключа. При редактировании пустое поле сохраняет уже заданный пароль.");
            project.ProtectedPrivateKey = project.ProtectedPassphrase = "";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(input.PrivateKey))
            {
                try
                {
                    using var key = new PrivateKeyFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input.PrivateKey)),
                        string.IsNullOrEmpty(input.Passphrase) ? null : input.Passphrase);
                }
                catch (Exception) { throw new DomainException("Не удалось прочитать SSH-ключ. Пример начала приватного ключа: -----BEGIN OPENSSH PRIVATE KEY-----. Вставьте весь приватный ключ, а не файл .pub, и укажите его пароль, если он установлен."); }
                project.ProtectedPrivateKey = secrets.Protect(input.PrivateKey);
                project.ProtectedPassphrase = secrets.Protect(input.Passphrase);
            }
            if (project.ProtectedPrivateKey == "")
                throw new DomainException("Нужен приватный SSH-ключ. Пример начала: -----BEGIN OPENSSH PRIVATE KEY-----. Для входа с паролем пользователя выберите способ «Логин и пароль».");
            project.ProtectedPassword = "";
        }
        project.AuthenticationType = input.AuthenticationType;
        project.Name = input.Name.Trim(); project.ConnectionId = connection.Id;
        project.RepositoryId = repository.Id; project.RepositoryName = repository.Name;
        project.RepositoryUrl = repository.CloneUrl; project.Branch = input.Branch;
        project.Host = host; project.Port = input.Port; project.Username = input.Username;
        project.HostFingerprint = input.HostFingerprint.Trim();
        project.WorkingDirectory = input.WorkingDirectory.TrimEnd('/');
        project.DeployCommand = input.DeployCommand; project.TimeoutMinutes = input.TimeoutMinutes;
        if (!id.HasValue) db.Projects.Add(project);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new DomainException("Репозиторий или сервер уже занят другим проектом.", 409); }
        return View(project, null);
    }

    public async Task DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        await mutationGate.WaitAsync(ct);
        try { await DeleteProjectCoreAsync(id, ct); }
        finally { mutationGate.Release(); }
    }
    private async Task DeleteProjectCoreAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await db.Projects.FindAsync([id], ct) ?? throw new DomainException("Проект не найден.", 404);
        if (await db.Deployments.AnyAsync(d => d.ProjectId == id &&
            (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Running), ct))
            throw new DomainException("Дождитесь завершения деплоя.", 409);
        db.Remove(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DeploymentView> EnqueueAsync(Guid projectId, CancellationToken ct = default)
    {
        await mutationGate.WaitAsync(ct);
        try { return await EnqueueCoreAsync(projectId, ct); }
        finally { mutationGate.Release(); }
    }
    private async Task<DeploymentView> EnqueueCoreAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking().Include(p => p.Connection).FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new DomainException("Проект не найден.", 404);
        if (await db.Deployments.AnyAsync(d => d.ProjectId == projectId &&
            (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Running), ct))
            throw new DomainException("Деплой этого проекта уже в очереди или выполняется.", 409);
        var run = new Deployment { ProjectId = projectId,
            ProtectedSnapshot = secrets.Protect(JsonSerializer.Serialize(new DeploymentSnapshot(project, project.Connection))) };
        db.Deployments.Add(run);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new DomainException("Деплой этого проекта уже в очереди или выполняется.", 409); }
        return View(run);
    }

    public async Task<List<DeploymentView>> HistoryAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Deployments.AsNoTracking().Where(d => d.ProjectId == id)
            .OrderByDescending(d => d.CreatedAt).Take(100)
            .Select(d => new DeploymentView(d.Id, d.ProjectId, d.Status, d.CreatedAt, d.StartedAt, d.FinishedAt,
                d.CommitSha, d.ExitCode, null)).ToListAsync(ct);
    }

    public async Task<DeploymentView> DeploymentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return View(await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new DomainException("Запуск не найден.", 404), true);
    }

    public async Task<DeploymentProject> GetTargetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new DomainException("Проект не найден.", 404);
    }

    public static void ValidateServer(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || port is < 1 or > 65535
            || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')))
            throw new DomainException("Укажите DNS-имя или IP-адрес сервера и корректный SSH-порт.");
    }
    private static void Validate(object input)
    {
        try { Validator.ValidateObject(input, new ValidationContext(input), true); }
        catch (ValidationException e) { throw new DomainException(e.Message); }
    }
    private static ConnectionView View(GitConnection c) => new(c.Id, c.Name, c.Provider, c.BaseUrl, c.Account, c.PublicOnly);
    private static ProjectView View(DeploymentProject p, DeploymentView? run, DeploymentView? successful = null) => new(p.Id, p.Name, p.ConnectionId,
        p.RepositoryId, p.RepositoryName, p.RepositoryUrl, p.Branch, p.Host, p.Port, p.Username,
        p.HostFingerprint, p.WorkingDirectory, p.DeployCommand, p.TimeoutMinutes, run, successful, p.AuthenticationType);
    public static DeploymentView View(Deployment d, bool log = false) => new(d.Id, d.ProjectId, d.Status,
        d.CreatedAt, d.StartedAt, d.FinishedAt, d.CommitSha, d.ExitCode, log ? d.Log : null);
}
