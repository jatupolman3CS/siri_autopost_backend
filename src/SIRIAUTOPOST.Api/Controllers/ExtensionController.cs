using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Extension;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>
/// The extension's own campaigns per paired device: settings, media, live state and log, and the buttons of its
/// settings page sent as commands (the device runs them on its next sync).
/// </summary>
[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class ExtensionController : ControllerBase
{
    /// <summary>Settings JSON up to 20 million characters.</summary>
    public const long MaxConfigBody = 64L * 1024 * 1024;
    /// <summary>A 200 MB video as a base64 data URL.</summary>
    public const long MaxImageBody = 300L * 1024 * 1024;

    public sealed record SaveConfigRequest(JsonElement Settings, int? BaseRevision);
    public sealed record ImageRequest(string Name, string Type, string Data);
    public sealed record CommandRequest(string Cmd, JsonElement? Args);

    [HttpGet("devices/{deviceId:guid}/config")]
    public Task<ExtensionConfigDto> Config(
        Guid wsId, Guid deviceId, [FromServices] IQueryHandler<GetExtensionConfigQuery, ExtensionConfigDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new GetExtensionConfigQuery(wsId, deviceId), ct);

    /// <summary>Saves the settings; 409 when they changed since <c>baseRevision</c> (null overwrites).</summary>
    [HttpPut("devices/{deviceId:guid}/config")]
    [RequestSizeLimit(MaxConfigBody)]
    public Task<ConfigSavedDto> SaveConfig(
        Guid wsId, Guid deviceId, SaveConfigRequest r,
        [FromServices] ICommandHandler<SaveExtensionConfigCommand, ConfigSavedDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SaveExtensionConfigCommand(wsId, deviceId, r.Settings, r.BaseRevision), ct);

    /// <summary>A photo or video of the campaigns (any device of the workspace).</summary>
    [HttpGet("extension-images/{imageId}")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Image(
        Guid wsId, string imageId, [FromServices] IQueryHandler<GetExtensionImageQuery, MediaContent> handler, CancellationToken ct)
    {
        var m = await handler.HandleAsync(new GetExtensionImageQuery(wsId, imageId), ct);
        return File(m.Data, m.ContentType, m.Name);
    }

    /// <summary>Adds a photo or video (data URL) under the extension's id; an existing id is kept as it is.</summary>
    [HttpPut("extension-images/{imageId}")]
    [RequestSizeLimit(MaxImageBody)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PutImage(
        Guid wsId, string imageId, ImageRequest r,
        [FromServices] ICommandHandler<PutExtensionImageCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new PutExtensionImageCommand(wsId, imageId, r.Name, r.Type, r.Data), ct);
        return NoContent();
    }

    [HttpGet("devices/{deviceId:guid}/live")]
    public Task<DeviceLiveDto> Live(
        Guid wsId, Guid deviceId, [FromServices] IQueryHandler<GetDeviceLiveQuery, DeviceLiveDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetDeviceLiveQuery(wsId, deviceId), ct);

    /// <summary>start, stop, runNow {campaignId}, testPost {campaignId, url, postId}, tgTest, tgFindChats, clearLogs, syncNow.</summary>
    [HttpPost("devices/{deviceId:guid}/commands")]
    public Task<DeviceCommandDto> SendCommand(
        Guid wsId, Guid deviceId, CommandRequest r,
        [FromServices] ICommandHandler<SendDeviceCommandCommand, DeviceCommandDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SendDeviceCommandCommand(wsId, deviceId, r.Cmd, r.Args), ct);

    [HttpGet("devices/{deviceId:guid}/commands/{commandId:guid}")]
    public Task<DeviceCommandDto> Command(
        Guid wsId, Guid deviceId, Guid commandId,
        [FromServices] IQueryHandler<GetDeviceCommandQuery, DeviceCommandDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetDeviceCommandQuery(wsId, deviceId, commandId), ct);

    [HttpDelete("devices/{deviceId:guid}/logs")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearLogs(
        Guid wsId, Guid deviceId, [FromServices] ICommandHandler<ClearDeviceLogsCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new ClearDeviceLogsCommand(wsId, deviceId), ct);
        return NoContent();
    }
}
