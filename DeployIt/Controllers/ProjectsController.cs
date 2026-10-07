using DeployIt.DTOs;
using DeployIt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployIt.Controllers;

[Authorize, ApiController, Route("api/projects")]
public sealed class ProjectsController(ProjectService projects, SshDeploymentService ssh) : ControllerBase
{
    [HttpGet] public Task<List<ProjectView>> List(CancellationToken ct) => projects.ProjectsAsync(ct);
    [HttpGet("{id:guid}")] public Task<ProjectView> Get(Guid id, CancellationToken ct) => projects.ProjectAsync(id, ct);
    [HttpPost] public async Task<ActionResult<ProjectView>> Create(ProjectInput input, CancellationToken ct)
        => Ok(await projects.SaveProjectAsync(input, ct: ct));
    [HttpPut("{id:guid}")] public async Task<ActionResult<ProjectView>> Update(Guid id, ProjectInput input, CancellationToken ct)
        => Ok(await projects.SaveProjectAsync(input, id, ct));
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    { await projects.DeleteProjectAsync(id, ct); return NoContent(); }
    [HttpPost("{id:guid}/deploy")] public async Task<IActionResult> Deploy(Guid id, CancellationToken ct)
    { var run = await projects.EnqueueAsync(id, ct); return Accepted($"/api/deployments/{run.Id}", run); }
    [HttpPost("{id:guid}/restart")] public async Task<IActionResult> Restart(Guid id, CancellationToken ct)
    { var run = await projects.EnqueueRestartAsync(id, ct); return Accepted($"/api/deployments/{run.Id}", run); }
    [HttpGet("{id:guid}/environment")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<EnvironmentFileView> Environment(Guid id, CancellationToken ct)
        => await ssh.ReadEnvironmentAsync(await projects.GetTargetAsync(id, ct), ct);
    [HttpPut("{id:guid}/environment")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<EnvironmentFileView> SaveEnvironment(Guid id, EnvironmentFileInput input, CancellationToken ct)
        => await ssh.SaveEnvironmentAsync(await projects.GetTargetAsync(id, ct), input, ct);
    [HttpGet("{id:guid}/deployments")] public Task<List<DeploymentView>> History(Guid id, CancellationToken ct)
        => projects.HistoryAsync(id, ct);
    [HttpPost("{id:guid}/check")] public async Task<IActionResult> Check(Guid id, CancellationToken ct)
        => Ok(new { message = await ssh.CheckAsync(await projects.GetTargetAsync(id, ct), ct) });
    [HttpPost("probe-host")] public async Task<IActionResult> Probe(HostProbeInput input, CancellationToken ct)
        => Ok(new { fingerprint = await ssh.ProbeAsync(input.Host, input.Port, ct) });
}
