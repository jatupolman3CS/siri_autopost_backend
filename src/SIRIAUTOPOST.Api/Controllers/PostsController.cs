using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Posts;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class PostsController : ControllerBase
{
    public sealed record ScheduleRequest(
        string Content,
        IReadOnlyList<Guid>? MediaIds,
        DateTimeOffset StartAt,
        bool UseDelay,
        string Repeat,
        IReadOnlyList<TargetSelection> Targets);

    /// <summary>Posts scheduled in [from, to) — at most 120 days.</summary>
    [HttpGet("posts")]
    public Task<IReadOnlyList<PostDto>> List(
        Guid wsId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to,
        [FromServices] IQueryHandler<GetPostsQuery, IReadOnlyList<PostDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetPostsQuery(wsId, from, to), ct);

    [HttpPost("posts/schedule")]
    [ProducesResponseType<ScheduleResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<ScheduleResultDto> Schedule(
        Guid wsId, ScheduleRequest r, [FromServices] ICommandHandler<SchedulePostsCommand, ScheduleResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SchedulePostsCommand(wsId, r.Content, r.MediaIds, r.StartAt, r.UseDelay, r.Repeat, r.Targets), ct);

    [HttpDelete("posts/{postId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid wsId, Guid postId, [FromServices] ICommandHandler<DeletePostCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeletePostCommand(wsId, postId), ct);
        return NoContent();
    }

    [HttpPost("posts/{postId:guid}/retry")]
    public Task<PostDto> Retry(
        Guid wsId, Guid postId, [FromServices] ICommandHandler<RetryPostCommand, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new RetryPostCommand(wsId, postId), ct);

    [HttpPost("posts/{postId:guid}/dismiss")]
    public Task<PostDto> Dismiss(
        Guid wsId, Guid postId, [FromServices] ICommandHandler<DismissPostErrorCommand, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new DismissPostErrorCommand(wsId, postId), ct);

    /// <summary>Open error reports: failed posts and posts awaiting group approval.</summary>
    [HttpGet("errors")]
    public Task<IReadOnlyList<PostDto>> Errors(
        Guid wsId, [FromServices] IQueryHandler<GetErrorsQuery, IReadOnlyList<PostDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetErrorsQuery(wsId), ct);
}
