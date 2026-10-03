using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>The owner's view of the browsers paired to a workspace.</summary>
[ApiController]
[Route("api/workspaces/{wsId:guid}/devices")]
[Produces("application/json")]
public sealed class DevicesController : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DeviceDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetDevicesQuery, IReadOnlyList<DeviceDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetDevicesQuery(wsId), ct);

    /// <summary>A code (valid 10 minutes) to type into the extension.</summary>
    [HttpPost("pairing")]
    public Task<PairingCodeDto> Pairing(
        Guid wsId, [FromServices] ICommandHandler<CreatePairingCodeCommand, PairingCodeDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreatePairingCodeCommand(wsId), ct);

    public sealed record UpdateDeviceRequest(string? Name, bool? JobsPaused);

    /// <summary>Renames the browser (and its Facebook account) or pauses the jobs it takes; null keeps a value.</summary>
    [HttpPut("{deviceId:guid}")]
    public Task<DeviceDto> Update(
        Guid wsId, Guid deviceId, UpdateDeviceRequest r,
        [FromServices] ICommandHandler<UpdateDeviceCommand, DeviceDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateDeviceCommand(wsId, deviceId, r.Name, r.JobsPaused), ct);

    [HttpDelete("{deviceId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(
        Guid wsId, Guid deviceId, [FromServices] ICommandHandler<RevokeDeviceCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new RevokeDeviceCommand(wsId, deviceId), ct);
        return NoContent();
    }
}
