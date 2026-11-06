using DeployIt.Models;

namespace DeployIt.Services;

public interface IProjectDiscovery
{
    Task<DiscoveryScan> DiscoverAsync(DeploymentProject target, string searchDirectory, CancellationToken ct);
}

public sealed record DiscoveryScan(List<DiscoveredProject> Projects, List<string> Warnings);
public sealed record DiscoveredProject(Guid Id, string WorkingDirectory, string RepositoryUrl,
    string Branch, RecoveryMetadata? Metadata);

// No SSH credentials, Git tokens, .env contents or encryption keys are copied here.
public sealed record RecoveryMetadata(int Version, Guid Id, string Name, string RepositoryId,
    string RepositoryName, string RepositoryUrl, string Branch, string DeployCommand,
    string RestartCommand, string StopCommand, int TimeoutMinutes)
{
    public static RecoveryMetadata From(DeploymentProject p) => new(1, p.Id, p.Name, p.RepositoryId,
        p.RepositoryName, p.RepositoryUrl, p.Branch, p.DeployCommand, p.RestartCommand, p.StopCommand, p.TimeoutMinutes);
}
