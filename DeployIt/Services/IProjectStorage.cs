using DeployIt.Models;
using DeployIt.DTOs;

namespace DeployIt.Services;

public interface IProjectStorage
{
    Task<EnvironmentFileView> SaveEnvironmentAsync(DeploymentProject project, EnvironmentFileInput input, CancellationToken ct);
    Task CleanReleasesAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, CancellationToken ct);
    Task DeleteAsync(DeploymentProject project, IReadOnlyCollection<Guid> knownReleases, CancellationToken ct);
}
