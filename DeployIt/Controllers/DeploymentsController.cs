using DeployIt.DTOs;
using DeployIt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployIt.Controllers;

[Authorize, ApiController, Route("api/deployments")]
public sealed class DeploymentsController(ProjectService projects) : ControllerBase
{
    [HttpGet("{id:guid}")] public Task<DeploymentView> Get(Guid id, CancellationToken ct) => projects.DeploymentAsync(id, ct);
    [HttpPost("{id:guid}/cancel")] public Task<DeploymentView> Cancel(Guid id, CancellationToken ct)
        => projects.CancelDeploymentAsync(id, ct);
}
