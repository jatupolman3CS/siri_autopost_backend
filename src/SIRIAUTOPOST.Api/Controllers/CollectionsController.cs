using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Collections;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Collections ("ชุดโพสต์"): named sets of library posts with composing settings and an approval flow.
// Viewers read, editors write, admins approve.
[ApiController]
[Route("api/workspaces/{wsId:guid}/collections")]
[Produces("application/json")]
public sealed class CollectionsController : ControllerBase
{
    public sealed record CreateCollectionRequest(string Name, string? Description);

    public sealed record UpdateCollectionRequest(string Name, string? Description, string? Icon, CollectionSettingsDto Settings);

    public sealed record SetActiveRequest(bool Active);
    public sealed record CollectionPostRequest(string Text, IReadOnlyList<Guid>? MediaIds);

    public sealed record CollectionPostsBatchRequest(IReadOnlyList<CollectionPostInput> Items);

    /// <param name="CollectionId">Move the post to this collection (null keeps it where it is).</param>
    public sealed record UpdateCollectionPostRequest(string Text, IReadOnlyList<Guid>? MediaIds, Guid? CollectionId);

    public sealed record ApprovalRequest(ApprovalAction Action);

    public sealed record AddExistingPostsRequest(IReadOnlyList<Guid> PostIds);

    [HttpGet]
    public Task<IReadOnlyList<CollectionDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetCollectionsQuery, IReadOnlyList<CollectionDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetCollectionsQuery(wsId), ct);

    [HttpPost]
    public Task<CollectionDto> Create(
        Guid wsId, CreateCollectionRequest r, [FromServices] ICommandHandler<CreateCollectionCommand, CollectionDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateCollectionCommand(wsId, r.Name, r.Description), ct);

    [HttpPut("{id:guid}")]
    public Task<CollectionDto> Update(
        Guid wsId, Guid id, UpdateCollectionRequest r, [FromServices] ICommandHandler<UpdateCollectionCommand, CollectionDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new UpdateCollectionCommand(wsId, id, r.Name, r.Description, r.Icon, r.Settings), ct);

    /// <summary>Deletes the collection (its posts stay in the library); 422 while a schedule uses it.</summary>
    [HttpPut("{id:guid}/active")]
    public Task<CollectionDto> SetActive(
        Guid wsId, Guid id, SetActiveRequest r, [FromServices] ICommandHandler<SetCollectionActiveCommand, CollectionDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetCollectionActiveCommand(wsId, id, r.Active), ct);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid wsId, Guid id, [FromServices] ICommandHandler<DeleteCollectionCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteCollectionCommand(wsId, id), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/posts")]
    public Task<CollectionPostDto> AddPost(
        Guid wsId, Guid id, CollectionPostRequest r, [FromServices] ICommandHandler<AddCollectionPostCommand, CollectionPostDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new AddCollectionPostCommand(wsId, id, r.Text, r.MediaIds), ct);

    /// <summary>Up to 20 posts at once (the AI writer); all or none.</summary>
    [HttpPost("{id:guid}/posts/batch")]
    public Task<IReadOnlyList<CollectionPostDto>> AddPosts(
        Guid wsId, Guid id, CollectionPostsBatchRequest r,
        [FromServices] ICommandHandler<AddCollectionPostsBatchCommand, IReadOnlyList<CollectionPostDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new AddCollectionPostsBatchCommand(wsId, id, r.Items), ct);

    /// <summary>Puts posts of the library into the collection (up to 500; the ones already in it stay as they are).</summary>
    [HttpPost("{id:guid}/posts/add")]
    public Task<CollectionDto> AddExistingPosts(
        Guid wsId, Guid id, AddExistingPostsRequest r, [FromServices] ICommandHandler<AddPostsToCollectionCommand, CollectionDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new AddPostsToCollectionCommand(wsId, id, r.PostIds), ct);

    [HttpPut("{id:guid}/posts/{postId:guid}")]
    public Task<CollectionPostDto> UpdatePost(
        Guid wsId, Guid id, Guid postId, UpdateCollectionPostRequest r,
        [FromServices] ICommandHandler<UpdateCollectionPostCommand, CollectionPostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateCollectionPostCommand(wsId, id, postId, r.Text, r.MediaIds, r.CollectionId), ct);

    /// <summary>Takes the post out of the collection; it stays in the library.</summary>
    [HttpDelete("{id:guid}/posts/{postId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePost(
        Guid wsId, Guid id, Guid postId, [FromServices] ICommandHandler<DeleteCollectionPostCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteCollectionPostCommand(wsId, id, postId), ct);
        return NoContent();
    }

    /// <summary>"request" (editor), "approve" or "reject" (admin).</summary>
    [HttpPost("{id:guid}/posts/{postId:guid}/approval")]
    public Task<CollectionPostDto> Approval(
        Guid wsId, Guid id, Guid postId, ApprovalRequest r,
        [FromServices] ICommandHandler<CollectionPostApprovalCommand, CollectionPostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CollectionPostApprovalCommand(wsId, id, postId, r.Action), ct);
}
