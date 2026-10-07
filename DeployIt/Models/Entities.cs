namespace DeployIt.Models;

public enum GitProvider { GitHub, GitLab }
public enum DeploymentStatus { Queued, Running, Succeeded, Failed, Interrupted, Canceled }
public enum SshAuthenticationType { PrivateKey, Password }
public enum DeploymentOperation { Deploy, Restart, Stop }

public sealed class GitConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public GitProvider Provider { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Account { get; set; } = "";
    public string ExternalUserId { get; set; } = "";
    public bool PublicOnly { get; set; }
    public string ProtectedToken { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeploymentProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public GitConnection Connection { get; set; } = null!;
    public string Name { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "deploy";
    public string HostFingerprint { get; set; } = "";
    public SshAuthenticationType AuthenticationType { get; set; } = SshAuthenticationType.PrivateKey;
    public string ProtectedPassword { get; set; } = "";
    public string ProtectedPrivateKey { get; set; } = "";
    public string ProtectedPassphrase { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string DeployCommand { get; set; } = "docker compose up -d --build --wait --wait-timeout 120";
    public string RestartCommand { get; set; } = "docker compose up -d --force-recreate --no-build";
    public string StopCommand { get; set; } = "docker compose stop";
    public int TimeoutMinutes { get; set; } = 20;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Deployment> Deployments { get; set; } = [];
}

public sealed class Deployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public DeploymentProject Project { get; set; } = null!;
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public DeploymentOperation Operation { get; set; } = DeploymentOperation.Deploy;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CommitSha { get; set; }
    public int? ExitCode { get; set; }
    public string Log { get; set; } = "";
    public bool LogTruncated { get; set; }
    public bool CancelRequested { get; set; }
    public string Stage { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string ProtectedSnapshot { get; set; } = "";
}

public sealed record DeploymentSnapshot(DeploymentProject Project, GitConnection Connection);
