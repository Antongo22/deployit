using DeployIt.DTOs;
using DeployIt.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeployIt.Controllers;

[Authorize, ApiController, Route("api/projects/{id:guid}/terminal")]
public sealed class TerminalController(ProjectService projects, SshDeploymentService ssh,
    AdminCredentials admin, IAntiforgery antiforgery, IHostApplicationLifetime application) : ControllerBase
{
    [HttpGet]
    public async Task Open(Guid id, CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            throw new DomainException("Откройте терминал кнопкой «Открыть SSH-терминал» на странице проекта.");
        if (!Uri.TryCreate(Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https")
            || !string.Equals(origin.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Терминал доступен только со страницы этой панели.", 403);
        var protocols = HttpContext.WebSockets.WebSocketRequestedProtocols;
        var csrf = protocols.FirstOrDefault(p => p.StartsWith("csrf.", StringComparison.Ordinal));
        if (!protocols.Contains("deployit-terminal") || csrf is null)
            throw new DomainException("Обновите страницу перед подключением к терминалу: отсутствует CSRF-токен.");
        Request.Headers["X-CSRF-TOKEN"] = csrf[5..];
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { throw new DomainException("Обновите страницу: проверка CSRF для терминала не пройдена."); }
        var project = await projects.GetTargetAsync(id, ct);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync("deployit-terminal");
        await ssh.RunTerminalAsync(project, socket, () => admin.IsValid(User), lifetime.Token);
    }
}
