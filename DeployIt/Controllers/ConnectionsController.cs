using System.ComponentModel.DataAnnotations;
using DeployIt.DTOs;
using DeployIt.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployIt.Controllers;

[Authorize, ApiController, Route("api/connections")]
public sealed class ConnectionsController(ProjectService projects) : ControllerBase
{
    [HttpGet] public Task<List<ConnectionView>> List(CancellationToken ct) => projects.ConnectionsAsync(ct);
    [HttpPost] public async Task<ActionResult<ConnectionView>> Create(ConnectionInput input, CancellationToken ct)
        => Ok(await projects.AddConnectionAsync(input, ct));
    [HttpGet("{id:guid}/repositories")] public Task<List<RepositoryView>> Repositories(Guid id, CancellationToken ct)
        => projects.RepositoriesAsync(id, ct);
    [HttpPut("{id:guid}/token")] public async Task<IActionResult> Token(Guid id, TokenInput input, CancellationToken ct)
    { await projects.UpdateTokenAsync(id, input.Token, ct); return NoContent(); }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    { await projects.DeleteConnectionAsync(id, ct); return NoContent(); }
}
public sealed record TokenInput([Required, StringLength(1000)] string Token);
