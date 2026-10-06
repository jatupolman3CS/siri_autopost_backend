using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Schedules;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Schedules ("ตารางโพสต์"): a collection, a link set and when to post. Their posts are queued a fortnight ahead and
// claimed by the paired browser. Viewers read, editors write.
[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class SchedulesController : ControllerBase
{
    public sealed record SetActiveRequest(bool Active);
    public sealed record RenameRequest(string Name);

    /// <param name="LinkId">The group to post to; with neither it and AccountId, the set's first usable link.</param>
    /// <param name="AccountId">Another account instead of a link (it posts to its default target).</param>
    /// <param name="CollectionPostId">The post to send; null = a random usable one.</param>
    /// <param name="Text">Replaces the post's text.</param>
    /// <param name="DeviceId">The extension that sends it (a workspace may have several); null = the one the link set names.</param>
    public sealed record TestPostRequest(
        Guid LinkSetId, Guid? LinkId, Guid? AccountId, Guid CollectionId, Guid? CollectionPostId, string? Text, Guid? DeviceId = null);

    /// <param name="Url">A Facebook group or page address.</param>
    /// <param name="MediaIds">Library images to attach (at most 10).</param>
    /// <param name="DeviceId">The extension that sends it; may be left out when only one is connected.</param>
    public sealed record ManualTestPostRequest(string Url, string Text, IReadOnlyList<Guid>? MediaIds, Guid? DeviceId);

    [HttpGet("schedules")]
    public Task<IReadOnlyList<ScheduleDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetSchedulesQuery, IReadOnlyList<ScheduleDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetSchedulesQuery(wsId), ct);

    /// <summary>
    /// Creates the schedule and queues its posts for the next 14 days (in the schedule's local calendar). 422 when the
    /// collection or link set is missing, there are 50 schedules already, nothing in the collection can be posted, or no
    /// connected Facebook account can post the set.
    /// </summary>
    [HttpPost("schedules")]
    [ProducesResponseType<ScheduleCreatedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<ScheduleCreatedDto> Create(
        Guid wsId, SaveScheduleRequest r, [FromServices] ICommandHandler<CreateScheduleCommand, ScheduleCreatedDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateScheduleCommand(wsId, r), ct);

    /// <summary>Pausing drops the posts still queued in the future; resuming queues the next 14 days again.</summary>
    [HttpPut("schedules/{id:guid}/active")]
    public Task<ScheduleDto> SetActive(
        Guid wsId, Guid id, SetActiveRequest r, [FromServices] ICommandHandler<SetScheduleActiveCommand, ScheduleDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetScheduleActiveCommand(wsId, id, r.Active), ct);

    /// <summary>Deletes the schedule and the posts it still has queued in the future.</summary>
    [HttpPut("schedules/{id:guid}/name")]
    public Task<ScheduleDto> Rename(
        Guid wsId, Guid id, RenameRequest r, [FromServices] ICommandHandler<RenameScheduleCommand, ScheduleDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new RenameScheduleCommand(wsId, id, r.Name), ct);

    [HttpDelete("schedules/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid wsId, Guid id, [FromServices] ICommandHandler<DeleteScheduleCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteScheduleCommand(wsId, id), ct);
        return NoContent();
    }

    /// <summary>The three hours of the day with the most published posts in the last 30 days, as "HH:00" in order.</summary>
    [HttpGet("schedules/best-times")]
    public Task<IReadOnlyList<string>> BestTimes(
        Guid wsId, [FromServices] IQueryHandler<GetBestTimesQuery, IReadOnlyList<string>> handler, CancellationToken ct,
        [FromQuery] int utcOffsetMinutes = 0) =>
        handler.HandleAsync(new GetBestTimesQuery(wsId, utcOffsetMinutes), ct);

    /// <summary>One real post, due now: the test page. The account must be connected to a browser.</summary>
    [HttpPost("test-post")]
    public Task<PostDto> TestPost(
        Guid wsId, TestPostRequest r, [FromServices] ICommandHandler<CreateTestPostCommand, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateTestPostCommand(wsId, r.LinkSetId, r.LinkId, r.AccountId, r.CollectionId, r.CollectionPostId, r.Text, r.DeviceId), ct);

    /// <summary>One real post from a typed address, text and library images, due now: tests that the jobs really reach an extension.</summary>
    [HttpPost("test-post/manual")]
    public Task<PostDto> ManualTestPost(
        Guid wsId, ManualTestPostRequest r, [FromServices] ICommandHandler<CreateManualTestPostCommand, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateManualTestPostCommand(wsId, r.Url, r.Text, r.MediaIds, r.DeviceId), ct);
}
