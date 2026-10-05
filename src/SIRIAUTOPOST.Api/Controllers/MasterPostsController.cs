using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.MasterPosts;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// The post library ("คลังโพสต์"): master posts that manage themselves (own switch, composing settings, timing limits,
// approval, results) and sit in any number of collections. Viewers read, editors write, admins approve.
[ApiController]
[Route("api/workspaces/{wsId:guid}/master-posts")]
[Produces("application/json")]
public sealed class MasterPostsController : ControllerBase
{
    /// <param name="CollectionIds">The collections to put the post in (none = it waits in the library).</param>
    /// <param name="Settings">The post's own settings; null = follow the collections.</param>
    /// <param name="Active">Defaults to on.</param>
    public sealed record CreateMasterPostRequest(
        string Text, IReadOnlyList<Guid>? MediaIds, IReadOnlyList<Guid>? CollectionIds, CollectionPostSettingsDto? Settings, bool? Active);

    /// <param name="CollectionIds">The collections the post sits in from now on; null keeps them.</param>
    /// <param name="Settings">The post's own settings; null keeps them.</param>
    public sealed record UpdateMasterPostRequest(
        string Text, IReadOnlyList<Guid>? MediaIds, IReadOnlyList<Guid>? CollectionIds, CollectionPostSettingsDto? Settings);

    public sealed record SetPostActiveRequest(bool Active);

    public sealed record PostApprovalRequest(ApprovalAction Action);

    /// <param name="CollectionId">For add_to_collection and remove_from_collection.</param>
    public sealed record BulkRequest(IReadOnlyList<Guid> PostIds, BulkPostAction Action, Guid? CollectionId);

    /// <summary>Every post of the workspace, oldest first, with the collections it sits in and what it made.</summary>
    [HttpGet]
    public Task<IReadOnlyList<CollectionPostDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetMasterPostsQuery, IReadOnlyList<CollectionPostDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetMasterPostsQuery(wsId), ct);

    /// <summary>The newest posts it made (default 50, at most 200): where each went, when and how it ended.</summary>
    [HttpGet("{id:guid}/activity")]
    public Task<IReadOnlyList<CollectionPostActivityDto>> Activity(
        Guid wsId, Guid id, [FromServices] IQueryHandler<GetMasterPostActivityQuery, IReadOnlyList<CollectionPostActivityDto>> handler,
        CancellationToken ct, [FromQuery] int take = 50) =>
        handler.HandleAsync(new GetMasterPostActivityQuery(wsId, id, take), ct);

    [HttpPost]
    public Task<CollectionPostDto> Create(
        Guid wsId, CreateMasterPostRequest r, [FromServices] ICommandHandler<CreateMasterPostCommand, CollectionPostDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateMasterPostCommand(wsId, r.Text, r.MediaIds, r.CollectionIds, r.Settings, r.Active ?? true), ct);

    /// <summary>Changes the text, media, own settings and collections; what the post queued for the future is made again.</summary>
    [HttpPut("{id:guid}")]
    public Task<CollectionPostDto> Update(
        Guid wsId, Guid id, UpdateMasterPostRequest r, [FromServices] ICommandHandler<UpdateMasterPostCommand, CollectionPostDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new UpdateMasterPostCommand(wsId, id, r.Text, r.MediaIds, r.CollectionIds, r.Settings), ct);

    /// <summary>Off: no schedule draws the post and what it queued for the future goes. On: the schedules fill in what is missing.</summary>
    [HttpPut("{id:guid}/active")]
    public Task<CollectionPostDto> SetActive(
        Guid wsId, Guid id, SetPostActiveRequest r, [FromServices] ICommandHandler<SetMasterPostActiveCommand, CollectionPostDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new SetMasterPostActiveCommand(wsId, id, r.Active), ct);

    /// <summary>Deletes the post for good: it leaves every collection and what it queued for the future is removed.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid wsId, Guid id, [FromServices] ICommandHandler<DeleteMasterPostCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteMasterPostCommand(wsId, id), ct);
        return NoContent();
    }

    /// <summary>"request" (editor), "approve" or "reject" (admin).</summary>
    [HttpPost("{id:guid}/approval")]
    public Task<CollectionPostDto> Approval(
        Guid wsId, Guid id, PostApprovalRequest r, [FromServices] ICommandHandler<MasterPostApprovalCommand, CollectionPostDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new MasterPostApprovalCommand(wsId, id, r.Action), ct);

    /// <summary>One action for up to 500 posts: switch on or off, delete, put into or take out of a collection.</summary>
    [HttpPost("bulk")]
    public Task<BulkMasterPostsResultDto> Bulk(
        Guid wsId, BulkRequest r, [FromServices] ICommandHandler<BulkMasterPostsCommand, BulkMasterPostsResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new BulkMasterPostsCommand(wsId, r.PostIds, r.Action, r.CollectionId), ct);
}
