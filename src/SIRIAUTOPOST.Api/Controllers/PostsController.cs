using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Posts.Commands;
using SIRIAUTOPOST.Application.Features.Posts.Queries;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/posts")]
[Produces("application/json")]
public sealed class PostsController : ControllerBase
{
    public sealed record UpdatePostRequest(string Content, DateTimeOffset? ScheduledAt);

    [HttpGet]
    public Task<IReadOnlyList<PostDto>> List(
        [FromServices] IQueryHandler<GetPostsQuery, IReadOnlyList<PostDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetPostsQuery(), ct);

    [HttpGet("{id:guid}", Name = nameof(GetById))]
    public Task<PostDto> GetById(
        Guid id, [FromServices] IQueryHandler<GetPostByIdQuery, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetPostByIdQuery(id), ct);

    [HttpPost]
    [ProducesResponseType<PostDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PostDto>> Create(
        CreatePostCommand command, [FromServices] ICommandHandler<CreatePostCommand, PostDto> handler, CancellationToken ct)
    {
        var post = await handler.HandleAsync(command, ct);
        return CreatedAtRoute(nameof(GetById), new { id = post.Id }, post);
    }

    [HttpPut("{id:guid}")]
    public Task<PostDto> Update(
        Guid id, UpdatePostRequest request, [FromServices] ICommandHandler<UpdatePostCommand, PostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdatePostCommand(id, request.Content, request.ScheduledAt), ct);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid id, [FromServices] ICommandHandler<DeletePostCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeletePostCommand(id), ct);
        return NoContent();
    }
}
