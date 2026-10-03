using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Api.Auth;
using SIRIAUTOPOST.Api.Extensions;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Devices;
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
    public sealed record HeartbeatRequest(string? Version);
    public sealed record GroupsRequest(IReadOnlyList<GroupLinkDto> Groups);
    public sealed record ResultRequest(bool Ok, bool AwaitingApproval, bool NeedsLogin, bool Blocked, string? Error);

    /// <summary>Trades a pairing code for the device key (shown once).</summary>
    [HttpPost("pair")]
    [AllowAnonymous]
    public Task<PairResultDto> Pair(
        PairDeviceCommand r, [FromServices] ICommandHandler<PairDeviceCommand, PairResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(r, ct);

    [HttpPost("heartbeat")]
    public Task<DeviceStatusDto> Heartbeat(
        HeartbeatRequest r, [FromServices] ICommandHandler<DeviceHeartbeatCommand, DeviceStatusDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new DeviceHeartbeatCommand(r.Version), ct);

    /// <summary>Replaces the groups of the device's Facebook account; returns how many it has.</summary>
    [HttpPut("groups")]
    public Task<int> Groups(
        GroupsRequest r, [FromServices] ICommandHandler<SyncDeviceGroupsCommand, int> handler, CancellationToken ct) =>
        handler.HandleAsync(new SyncDeviceGroupsCommand(r.Groups ?? []), ct);

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
        handler.HandleAsync(new ReportJobResultCommand(postId, r.Ok, r.AwaitingApproval, r.NeedsLogin, r.Blocked, r.Error), ct);

    [HttpGet("media/{mediaId:guid}")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Media(
        Guid mediaId, [FromServices] IQueryHandler<GetDeviceMediaQuery, MediaContent> handler, CancellationToken ct)
    {
        var m = await handler.HandleAsync(new GetDeviceMediaQuery(mediaId), ct);
        return File(m.Data, m.ContentType, m.Name);
    }
}
