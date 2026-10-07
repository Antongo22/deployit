using System.ComponentModel.DataAnnotations;
using DeployIt.Models;

namespace DeployIt.DTOs;

public sealed class ConnectionInput
{
    [Required(ErrorMessage = "Укажите название подключения. Пример: Рабочий GitHub."),
     StringLength(100, ErrorMessage = "Название подключения должно быть не длиннее 100 символов. Пример: Рабочий GitHub.")]
    public string Name { get; set; } = "";
    public GitProvider Provider { get; set; }
    [Required(ErrorMessage = "Укажите адрес Git-сервиса. Примеры: https://github.com или https://gitlab.com. Репозиторий выбирается при создании проекта."),
     Url(ErrorMessage = "Нужен полный HTTPS-адрес Git-сервиса. Примеры: https://github.com или https://gitlab.com. Ссылку на репозиторий указывать здесь не нужно.")]
    public string BaseUrl { get; set; } = "https://github.com";
    public bool PublicOnly { get; set; }
    [StringLength(100, ErrorMessage = "Имя аккаунта должно быть не длиннее 100 символов. Пример: octocat. Вставлять ссылку на репозиторий не нужно.")]
    public string Account { get; set; } = "";
    [StringLength(1000, ErrorMessage = "Access token должен быть не длиннее 1000 символов. Вставьте только значение токена из настроек Git-сервиса, без SSH-ключа или файла конфигурации.")]
    public string Token { get; set; } = "";
}

public sealed class ProjectInput
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public Guid ConnectionId { get; set; }
    [Required] public string RepositoryId { get; set; } = "";
    [Required, StringLength(200)] public string Branch { get; set; } = "main";
    [Required, StringLength(253)] public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 22;
    [Required, StringLength(100)] public string Username { get; set; } = "deploy";
    [Required, StringLength(100)] public string HostFingerprint { get; set; } = "";
    public SshAuthenticationType AuthenticationType { get; set; } = SshAuthenticationType.PrivateKey;
    [StringLength(1000, ErrorMessage = "Пароль SSH-пользователя должен быть не длиннее 1000 символов. Вставьте только пароль пользователя на сервере, без SSH-ключа.")]
    public string Password { get; set; } = "";
    [StringLength(20000)] public string PrivateKey { get; set; } = "";
    [StringLength(500)] public string Passphrase { get; set; } = "";
    [Required, StringLength(500)] public string WorkingDirectory { get; set; } = "/opt/deployit/app";
    [Required, StringLength(10000)] public string DeployCommand { get; set; } = "docker compose up -d --build";
    [Required(ErrorMessage = "Укажите команду перезапуска. Пример для Compose: docker compose up -d --force-recreate --no-build."), StringLength(10000)]
    public string RestartCommand { get; set; } = "docker compose up -d --force-recreate --no-build";
    [Range(1, 120)] public int TimeoutMinutes { get; set; } = 20;
}

public sealed class HostProbeInput
{
    [Required, StringLength(253)] public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = 22;
}

public sealed record ConnectionView(Guid Id, string Name, GitProvider Provider, string BaseUrl,
    string Account, bool PublicOnly);
public sealed record RepositoryView(string Id, string Name, string CloneUrl, string DefaultBranch);
public sealed record DeploymentView(Guid Id, Guid ProjectId, DeploymentStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? CommitSha, int? ExitCode, string? Log, DeploymentOperation Operation = DeploymentOperation.Deploy,
    string Stage = "", string? ErrorMessage = null, bool CancelRequested = false);
public sealed record ProjectView(Guid Id, string Name, Guid ConnectionId, string RepositoryId,
    string RepositoryName, string RepositoryUrl, string Branch, string Host, int Port, string Username,
    string HostFingerprint, string WorkingDirectory, string DeployCommand, int TimeoutMinutes,
    DeploymentView? LatestDeployment, DeploymentView? LastSuccessfulDeployment = null,
    SshAuthenticationType AuthenticationType = SshAuthenticationType.PrivateKey,
    string RestartCommand = "docker compose up -d --force-recreate --no-build");

public sealed class EnvironmentFileInput
{
    [Required(ErrorMessage = "Сначала загрузите .env с сервера, затем сохраните изменения.")]
    public string Version { get; set; } = "";
    [StringLength(32768, ErrorMessage = "Файл .env должен быть не длиннее 32 768 символов.")]
    public string Content { get; set; } = "";
}

public sealed record EnvironmentFileView(string Path, bool Exists, string Content, string Version);

public sealed class DomainException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
