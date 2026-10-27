using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.LineOas;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>The master list of LINE Official Accounts: paste an OA address, a browser reads its customers.</summary>
[ApiController]
[Route("api/workspaces/{wsId:guid}/line/oas")]
[Produces("application/json")]
public sealed class LineOasController : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<LineOaDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetLineOasQuery, IReadOnlyList<LineOaDto>> handler = null!, CancellationToken ct = default) =>
        handler.HandleAsync(new GetLineOasQuery(wsId), ct);

    [HttpPost]
    public Task<LineOaDto> Add(
        Guid wsId, [FromBody] AddLineOaRequest r,
        [FromServices] ICommandHandler<AddLineOaCommand, LineOaDto> handler = null!, CancellationToken ct = default) =>
        handler.HandleAsync(new AddLineOaCommand(wsId, r.Url, r.DeviceId, r.Name), ct);

    [HttpPost("{id:guid}/sync")]
    public async Task<IActionResult> Sync(
        Guid wsId, Guid id, [FromServices] ICommandHandler<SyncLineOaCommand, bool> handler = null!, CancellationToken ct = default)
    {
        await handler.HandleAsync(new SyncLineOaCommand(wsId, id), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/use")]
    public async Task<IActionResult> Use(
        Guid wsId, Guid id, [FromServices] ICommandHandler<UseLineOaCommand, bool> handler = null!, CancellationToken ct = default)
    {
        await handler.HandleAsync(new UseLineOaCommand(wsId, id), ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(
        Guid wsId, Guid id, [FromServices] ICommandHandler<RemoveLineOaCommand, bool> handler = null!, CancellationToken ct = default)
    {
        await handler.HandleAsync(new RemoveLineOaCommand(wsId, id), ct);
        return NoContent();
    }
}
