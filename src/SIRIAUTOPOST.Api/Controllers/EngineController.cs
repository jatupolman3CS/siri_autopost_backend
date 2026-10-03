using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Engine;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Posting-engine settings of a workspace (anti-ban, offline policy) and the extension connection state.
[ApiController]
[Route("api/workspaces/{wsId:guid}/engine")]
[Produces("application/json")]
public sealed class EngineController : ControllerBase
{
    public sealed record ExtensionStateRequest(bool Online);

    [HttpGet]
    public Task<EngineSettingsDto> Get(
        Guid wsId, [FromServices] IQueryHandler<GetEngineSettingsQuery, EngineSettingsDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetEngineSettingsQuery(wsId), ct);

    [HttpPut("anti-ban")]
    public Task<EngineSettingsDto> UpdateAntiBan(
        Guid wsId, AntiBanDto settings, [FromServices] ICommandHandler<UpdateAntiBanCommand, EngineSettingsDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateAntiBanCommand(wsId, settings), ct);

    [HttpPut("offline")]
    public Task<EngineSettingsDto> UpdateOffline(
        Guid wsId, OfflineDto settings, [FromServices] ICommandHandler<UpdateOfflineCommand, EngineSettingsDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateOfflineCommand(wsId, settings), ct);

    /// <summary>Offline simulation from the dashboard (until extensions report heartbeats).</summary>
    [HttpPost("extension")]
    public Task<ExtensionStateDto> SetExtension(
        Guid wsId, ExtensionStateRequest r, [FromServices] ICommandHandler<SetExtensionOnlineCommand, ExtensionStateDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetExtensionOnlineCommand(wsId, r.Online), ct);

    [HttpPost("waiting/skip")]
    public Task<ExtensionStateDto> SkipWaiting(
        Guid wsId, [FromServices] ICommandHandler<SkipWaitingPostsCommand, ExtensionStateDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SkipWaitingPostsCommand(wsId), ct);
}
