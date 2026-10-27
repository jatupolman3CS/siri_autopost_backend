using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Api.Auth;
using SIRIAUTOPOST.Api.Extensions;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Features.Extension;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>
/// What the browser extension calls, authenticated with X-Device-Key (from POST pair). Polling order:
/// heartbeat, then jobs/claim; after posting, jobs/{id}/result.
/// </summary>
[ApiController]
[Route("api/device")]
[Produces("application/json")]
[EnableCors(ServiceCollectionExtensions.DeviceCors)]
[Authorize(AuthenticationSchemes = DeviceKeyAuthenticationHandler.SchemeName)]
public sealed class DeviceApiController : ControllerBase
{
    public sealed record LineHeartbeatRequest(bool Ready, string? BotId = null, string? OaName = null);
    public sealed record HeartbeatRequest(string? Version, bool? SessionOk = null, LineHeartbeatRequest? Line = null, bool? LineSessionOk = null);
    public sealed record GroupsRequest(IReadOnlyList<GroupLinkDto> Groups);
    public sealed record SyncLineChatDto(string UserId, string Name, IReadOnlyList<string>? Labels = null, DateTimeOffset? LastMessageAt = null, bool? Unread = null);
    public sealed record SyncLineChatsRequest(string BotId, string OaName, bool Complete, IReadOnlyList<SyncLineChatDto> Chats);
    /// <param name="PostUrl">Where the post went up on Facebook, when the extension could read it (a bump opens it).</param>
    /// <param name="Shot">A picture of the posting window (a JPEG/PNG data URL, 2 MB at most) for the notification, sent when the job asked for one.</param>
    public sealed record ResultRequest(
        bool Ok, bool AwaitingApproval, bool NeedsLogin, bool Blocked, string? Error,
        string? PostUrl = null, string? Shot = null,
        bool Skip = false, int? DeferMinutes = null, string? Reason = null);
    public sealed record BumpResultRequest(bool Ok, bool NeedsLogin, bool Blocked, string? Error);
    /// <param name="Wait">With takeCommands: hold the call (up to 25 s) until the web app sends a command.</param>
    public sealed record SyncRequest(string? Version, JsonElement? State, IReadOnlyList<DeviceLogEntry>? Logs, bool TakeCommands, bool Wait = false);
    public sealed record ConfigRequest(JsonElement Settings, int? BaseRevision);
    public sealed record IdsRequest(IReadOnlyList<string>? Ids);
    public sealed record CommandResultRequest(JsonElement? Result);

    /// <summary>Trades a pairing code for the device key (shown once).</summary>
    [HttpPost("pair")]
    [AllowAnonymous]
    public Task<PairResultDto> Pair(
        PairDeviceCommand r, [FromServices] ICommandHandler<PairDeviceCommand, PairResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(r, ct);

    [HttpPost("heartbeat")]
    public Task<DeviceStatusDto> Heartbeat(
        HeartbeatRequest r, [FromServices] ICommandHandler<DeviceHeartbeatCommand, DeviceStatusDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new DeviceHeartbeatCommand(
            r.Version,
            r.SessionOk,
            r.Line is { } l ? new LineHeartbeatDto(l.Ready, l.BotId, l.OaName) : null,
            r.LineSessionOk), ct);

    /// <summary>Replaces the groups of the device's Facebook account; returns how many it has.</summary>
    [HttpPut("groups")]
    public Task<int> Groups(
        GroupsRequest r, [FromServices] ICommandHandler<SyncDeviceGroupsCommand, int> handler, CancellationToken ct) =>
        handler.HandleAsync(new SyncDeviceGroupsCommand(r.Groups ?? []), ct);

    /// <summary>Replaces the roster chats of the device's LINE account; returns how many it has.</summary>
    [HttpPut("line/chats")]
    public Task<int> LineChats(
        SyncLineChatsRequest r, [FromServices] ICommandHandler<SyncLineChatsCommand, int> handler, CancellationToken ct) =>
        handler.HandleAsync(new SyncLineChatsCommand(
            r.BotId,
            r.OaName,
            r.Complete,
            (r.Chats ?? []).Select(c => new Application.Features.Devices.SyncLineChatDto(c.UserId, c.Name, c.Labels, c.LastMessageAt, c.Unread)).ToList()), ct);

    /// <summary>The next post to publish now, or 204 when there is none.</summary>
    [HttpPost("jobs/claim")]
    [ProducesResponseType<JobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Claim(
        [FromServices] ICommandHandler<ClaimJobCommand, JobDto?> handler, CancellationToken ct)
    {
        var job = await handler.HandleAsync(new ClaimJobCommand(), ct);
        return job is null ? NoContent() : Ok(job);
    }

    [HttpPost("jobs/{postId:guid}/result")]
    public Task<PostDto> Result(
        Guid postId, ResultRequest r, [FromServices] ICommandHandler<ReportJobResultCommand, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ReportJobResultCommand(
            postId, r.Ok, r.AwaitingApproval, r.NeedsLogin, r.Blocked, r.Error,
            r.PostUrl, ShotImage.Parse(r.Shot), r.Skip, r.DeferMinutes, r.Reason), ct);

    /// <summary>The result of a bump job (<c>kind: "bump"</c> from jobs/claim): the comment was made, or why not.</summary>
    [HttpPost("bumps/{bumpId:guid}/result")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> BumpResult(
        Guid bumpId, BumpResultRequest r, [FromServices] ICommandHandler<ReportBumpResultCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new ReportBumpResultCommand(bumpId, r.Ok, r.NeedsLogin, r.Blocked, r.Error), ct);
        return NoContent();
    }

    [HttpGet("media/{mediaId:guid}")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Media(
        Guid mediaId, [FromServices] IQueryHandler<GetDeviceMediaQuery, MediaContent> handler, [FromServices] IHttpClientFactory http, [FromServices] IObjectStorage storage, CancellationToken ct)
    {
        var m = await handler.HandleAsync(new GetDeviceMediaQuery(mediaId), ct);
        return await this.ToDeviceResultAsync(m, http, storage, ct);
    }

    // ---------- the extension's own campaigns, edited in the web app ----------

    /// <summary>
    /// Every 30 seconds: state (when it changed) and new log lines in; the server's settings revision and,
    /// with takeCommands, the web app's waiting commands out. With wait, the call stays open up to 25 s until a
    /// command arrives (long polling), so the extension can loop on it and react to the web app at once.
    /// </summary>
    [HttpPost("sync")]
    public Task<DeviceSyncDto> Sync(
        SyncRequest r, [FromServices] ICommandHandler<DeviceSyncCommand, DeviceSyncDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new DeviceSyncCommand(r.Version, r.State, r.Logs, r.TakeCommands, r.Wait), ct);

    [HttpGet("config")]
    public Task<ExtensionConfigDto> Config(
        [FromServices] IQueryHandler<GetOwnExtensionConfigQuery, ExtensionConfigDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetOwnExtensionConfigQuery(), ct);

    /// <summary>Uploads this browser's settings; 409 when the web app changed them since <c>baseRevision</c>.</summary>
    [HttpPut("config")]
    [RequestSizeLimit(ExtensionController.MaxConfigBody)]
    public Task<ConfigSavedDto> SaveConfig(
        ConfigRequest r, [FromServices] ICommandHandler<SaveOwnExtensionConfigCommand, ConfigSavedDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SaveOwnExtensionConfigCommand(r.Settings, r.BaseRevision), ct);

    /// <summary>The ids among <c>ids</c> the server does not have yet.</summary>
    [HttpPost("images/missing")]
    public Task<MissingImagesDto> MissingImages(
        IdsRequest r, [FromServices] IQueryHandler<GetMissingImagesQuery, MissingImagesDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetMissingImagesQuery(r.Ids ?? []), ct);

    /// <summary>One media file in the extension's record shape ({ name, type, data: data URL }).</summary>
    [HttpGet("images/{imageId}")]
    public Task<ExtensionImageDto> Image(
        string imageId, [FromServices] IQueryHandler<GetOwnExtensionImageQuery, ExtensionImageDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetOwnExtensionImageQuery(imageId), ct);

    [HttpPut("images/{imageId}")]
    [RequestSizeLimit(ExtensionController.MaxImageBody)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> PutImage(
        string imageId, ExtensionImageDto r, [FromServices] ICommandHandler<PutOwnExtensionImageCommand, Unit> handler,
        CancellationToken ct)
    {
        await handler.HandleAsync(new PutOwnExtensionImageCommand(imageId, r.Name, r.Type, r.Data), ct);
        return NoContent();
    }

    [HttpPost("commands/{commandId:guid}/result")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CommandResult(
        Guid commandId, CommandResultRequest r, [FromServices] ICommandHandler<ReportCommandResultCommand, Unit> handler,
        CancellationToken ct)
    {
        await handler.HandleAsync(new ReportCommandResultCommand(commandId, r.Result), ct);
        return NoContent();
    }
}
