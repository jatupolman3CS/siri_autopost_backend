using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Collections;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.MasterPosts;

// The post library ("คลังโพสต์"): every post is a master post that manages itself (its own switch, composing settings,
// timing limits, approval and results) and sits in any number of collections. Viewers read, editors write, admins approve.

public sealed record GetMasterPostsQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<CollectionPostDto>>;

/// <summary>Every post of the workspace, oldest first, with its collections and what it made.</summary>
public sealed class GetMasterPostsQueryHandler(IWorkspaceRepository workspaces, CollectionPostViews views, ICurrentUser current)
    : IQueryHandler<GetMasterPostsQuery, IReadOnlyList<CollectionPostDto>>
{
    public async Task<IReadOnlyList<CollectionPostDto>> HandleAsync(GetMasterPostsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return await views.ListAsync(q.WorkspaceId, ct);
    }
}

public sealed record GetMasterPostActivityQuery(Guid WorkspaceId, Guid PostId, int Take) : IQuery<IReadOnlyList<CollectionPostActivityDto>>;

/// <summary>The newest posts a master post made (where it went, when, how it ended).</summary>
public sealed class GetMasterPostActivityQueryHandler(
    IWorkspaceRepository workspaces, ICollectionPostRepository collectionPosts, IPostRepository posts, ICurrentUser current)
    : IQueryHandler<GetMasterPostActivityQuery, IReadOnlyList<CollectionPostActivityDto>>
{
    public const int MaxTake = 200;

    public async Task<IReadOnlyList<CollectionPostActivityDto>> HandleAsync(GetMasterPostActivityQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var post = await MasterPostLookups.RequireAsync(collectionPosts, q.WorkspaceId, q.PostId, ct);
        var list = await posts.ListRecentByCollectionPostAsync(q.WorkspaceId, post.Id, Math.Clamp(q.Take, 1, MaxTake), ct);
        return list.Select(CollectionPostActivityDto.From).ToList();
    }
}

/// <param name="Settings">Null = no own settings (follow the collections).</param>
/// <param name="CollectionIds">The collections to put the post in (none = it waits in the library).</param>
public sealed record CreateMasterPostCommand(
    Guid WorkspaceId, string Text, IReadOnlyList<Guid>? MediaIds, IReadOnlyList<Guid>? CollectionIds, CollectionPostSettingsDto? Settings, bool Active)
    : ICommand<CollectionPostDto>;

public sealed class CreateMasterPostCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, IMediaRepository media,
    CollectionPostViews views, PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateMasterPostCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(CreateMasterPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var into = await MasterPostLookups.RequireCollectionsAsync(collections, c.WorkspaceId, c.CollectionIds ?? [], ct);
        var now = clock.GetUtcNow();
        var post = CollectionPost.Create(c.WorkspaceId, c.Text, c.MediaIds, into.Any(x => x.Settings.RequireApproval), now);
        if (c.Settings is not null) post.UpdateSettings(c.Settings.ToSettings(), now);
        post.SetActive(c.Active, now);
        await CollectionLookups.EnsureMediaExistAsync(media, c.WorkspaceId, post.MediaIds, ct);
        await CollectionLookups.EnsureRoomForPostsAsync(posts, c.WorkspaceId, 1, ct);
        await quotas.EnsureLibraryPostsAsync(c.WorkspaceId, 1, ct);
        posts.Add(post);
        posts.AddMembers(into.Select(x => CollectionMember.Create(c.WorkspaceId, x.Id, post.Id, now)));
        await uow.SaveChangesAsync(ct);
        return await views.OneAsync(post, ct);
    }
}

/// <summary>
/// The post's form: text, media, its own settings and the collections it sits in. <paramref name="CollectionIds"/> and
/// <paramref name="Settings"/> null keep what the post has. A change of text, media, settings or collections reaches the
/// posts the schedules still have queued for the future: those of the old version go and the schedules make them again.
/// </summary>
public sealed record UpdateMasterPostCommand(
    Guid WorkspaceId, Guid PostId, string Text, IReadOnlyList<Guid>? MediaIds, IReadOnlyList<Guid>? CollectionIds,
    CollectionPostSettingsDto? Settings) : ICommand<CollectionPostDto>;

public sealed class UpdateMasterPostCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, IMediaRepository media,
    CollectionPostViews views, CollectionPostSync sync, ScheduleTopUp topUp, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateMasterPostCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(UpdateMasterPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await MasterPostLookups.RequireAsync(posts, c.WorkspaceId, c.PostId, ct);
        await CollectionLookups.EnsureMediaExistAsync(media, c.WorkspaceId, c.MediaIds, ct);
        var now = clock.GetUtcNow();

        var old = await posts.ListMembersOfPostAsync(c.WorkspaceId, post.Id, ct);
        var oldIds = old.Select(m => m.CollectionId).ToHashSet();
        var wanted = c.CollectionIds is null
            ? await MasterPostLookups.RequireCollectionsAsync(collections, c.WorkspaceId, oldIds, ct)
            : await MasterPostLookups.RequireCollectionsAsync(collections, c.WorkspaceId, c.CollectionIds, ct);
        var wantedIds = wanted.Select(x => x.Id).ToHashSet();

        var before = (post.Text, Media: post.MediaIds.ToList(), Settings: post.Settings.Copy());
        post.Edit(c.Text, c.MediaIds, wanted.Any(x => x.Settings.RequireApproval), now);
        if (c.Settings is not null) post.UpdateSettings(c.Settings.ToSettings(), now);
        var contentChanged = before.Text != post.Text || !before.Media.SequenceEqual(post.MediaIds) || !SameSettings(before.Settings, post.Settings);

        var removed = oldIds.Except(wantedIds).ToList();
        var added = wantedIds.Except(oldIds).ToList();
        posts.RemoveMembers(old.Where(m => removed.Contains(m.CollectionId)));
        posts.AddMembers(added.Select(id => CollectionMember.Create(c.WorkspaceId, id, post.Id, now)));

        // Anything the post made for the future is stale when its content changed; when only its places changed, just
        // the collections it left lose it and the ones it joined look again.
        var affected = (await sync.ChangedAsync(c.WorkspaceId, [post.Id], contentChanged ? oldIds.Union(wantedIds) : removed, dropQueued: true, now, ct)).ToList();
        if (!contentChanged && added.Count > 0)
            affected.AddRange(await sync.ChangedAsync(c.WorkspaceId, [post.Id], added, dropQueued: false, now, ct));
        await uow.SaveChangesAsync(ct);
        await SourceTopUp.RunAsync(topUp, c.WorkspaceId, affected.DistinctBy(s => s.Id).ToList(), ct);
        return await views.OneAsync(post, ct);
    }

    private static bool SameSettings(CollectionPostSettings a, CollectionPostSettings b) =>
        a.Hashtags == b.Hashtags && a.Footer == b.Footer && a.FooterPos == b.FooterPos && a.ValidFrom == b.ValidFrom && a.ValidUntil == b.ValidUntil &&
        a.TimeFrom == b.TimeFrom && a.TimeTo == b.TimeTo && a.MaxPerDay == b.MaxPerDay && a.Weekdays.SequenceEqual(b.Weekdays);
}

/// <summary>
/// Switches one post on or off. Off: no schedule draws it and what it already queued for the future goes. On: the schedules
/// of its collections fill in what is missing.
/// </summary>
public sealed record SetMasterPostActiveCommand(Guid WorkspaceId, Guid PostId, bool Active) : ICommand<CollectionPostDto>;

public sealed class SetMasterPostActiveCommandHandler(
    IWorkspaceRepository workspaces, ICollectionPostRepository posts, CollectionPostViews views, CollectionPostSync sync, ScheduleTopUp topUp,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetMasterPostActiveCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(SetMasterPostActiveCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await MasterPostLookups.RequireAsync(posts, c.WorkspaceId, c.PostId, ct);
        if (post.Active != c.Active)
        {
            var now = clock.GetUtcNow();
            post.SetActive(c.Active, now);
            var collectionIds = (await posts.ListMembersOfPostAsync(c.WorkspaceId, post.Id, ct)).Select(m => m.CollectionId).ToList();
            var affected = await sync.ChangedAsync(c.WorkspaceId, [post.Id], collectionIds, dropQueued: !c.Active, now, ct);
            await uow.SaveChangesAsync(ct);
            await SourceTopUp.RunAsync(topUp, c.WorkspaceId, affected, ct);
        }
        return await views.OneAsync(post, ct);
    }
}

/// <summary>Deletes a post for good: it leaves every collection and what it queued for the future is removed.</summary>
public sealed record DeleteMasterPostCommand(Guid WorkspaceId, Guid PostId) : ICommand<Unit>;

public sealed class DeleteMasterPostCommandHandler(
    IWorkspaceRepository workspaces, ICollectionPostRepository posts, CollectionPostSync sync, ScheduleTopUp topUp, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeleteMasterPostCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteMasterPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await MasterPostLookups.RequireAsync(posts, c.WorkspaceId, c.PostId, ct);
        var collectionIds = (await posts.ListMembersOfPostAsync(c.WorkspaceId, post.Id, ct)).Select(m => m.CollectionId).ToList();
        var affected = await sync.ChangedAsync(c.WorkspaceId, [post.Id], collectionIds, dropQueued: true, clock.GetUtcNow(), ct);
        posts.Remove(post);
        await uow.SaveChangesAsync(ct);
        await SourceTopUp.RunAsync(topUp, c.WorkspaceId, affected, ct);
        return Unit.Value;
    }
}

/// <summary>An editor asks for approval; an admin approves or sends the post back to draft.</summary>
public sealed record MasterPostApprovalCommand(Guid WorkspaceId, Guid PostId, ApprovalAction Action) : ICommand<CollectionPostDto>;

public sealed class MasterPostApprovalCommandHandler(
    IWorkspaceRepository workspaces, ICollectionPostRepository posts, CollectionPostViews views, CollectionPostSync sync, ScheduleTopUp topUp,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<MasterPostApprovalCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(MasterPostApprovalCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, c.Action == ApprovalAction.Request ? WorkspaceRole.Editor : WorkspaceRole.Admin, ct);
        var post = await MasterPostLookups.RequireAsync(posts, c.WorkspaceId, c.PostId, ct);
        var now = clock.GetUtcNow();
        switch (c.Action)
        {
            case ApprovalAction.Request: post.RequestApproval(now); break;
            case ApprovalAction.Approve: post.Approve(now); break;
            default: post.Reject(now); break;
        }
        // Approved: the schedules of its collections that require approval may use it now. Sent back: what it queued is not approved.
        var collectionIds = (await posts.ListMembersOfPostAsync(c.WorkspaceId, post.Id, ct)).Select(m => m.CollectionId).ToList();
        var affected = await sync.ChangedAsync(c.WorkspaceId, [post.Id], collectionIds, dropQueued: c.Action == ApprovalAction.Reject, now, ct);
        await uow.SaveChangesAsync(ct);
        await SourceTopUp.RunAsync(topUp, c.WorkspaceId, affected, ct);
        return await views.OneAsync(post, ct);
    }
}

/// <summary>What the bulk bar does to the posts it has selected.</summary>
public enum BulkPostAction
{
    Activate,
    Deactivate,
    Delete,
    AddToCollection,
    RemoveFromCollection,
}

/// <summary>One action for up to 500 posts of the library at once (the management screen's selection bar).</summary>
/// <param name="CollectionId">For AddToCollection and RemoveFromCollection.</param>
public sealed record BulkMasterPostsCommand(Guid WorkspaceId, IReadOnlyList<Guid> PostIds, BulkPostAction Action, Guid? CollectionId)
    : ICommand<BulkMasterPostsResultDto>;

/// <param name="Changed">Posts the action really changed (a post already on is not counted for "activate").</param>
public sealed record BulkMasterPostsResultDto(int Changed);

public sealed class BulkMasterPostsCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, CollectionPostSync sync, ScheduleTopUp topUp,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<BulkMasterPostsCommand, BulkMasterPostsResultDto>
{
    public const int MaxItems = 500;

    public async Task<BulkMasterPostsResultDto> HandleAsync(BulkMasterPostsCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var ids = c.PostIds.Distinct().ToList();
        var found = (await posts.ListByIdsAsync(c.WorkspaceId, ids, ct)).ToList(); // ids of another workspace are ignored
        if (found.Count == 0) return new BulkMasterPostsResultDto(0);
        var now = clock.GetUtcNow();
        var memberships = (await posts.ListMembersAsync(c.WorkspaceId, ct)).Where(m => ids.Contains(m.PostId)).ToList();
        var affected = new List<Schedule>();
        var changed = 0;

        switch (c.Action)
        {
            case BulkPostAction.Activate:
            case BulkPostAction.Deactivate:
            {
                var on = c.Action == BulkPostAction.Activate;
                var posts2 = found.Where(p => p.Active != on).ToList();
                foreach (var p in posts2) p.SetActive(on, now);
                changed = posts2.Count;
                affected.AddRange(await sync.ChangedAsync(
                    c.WorkspaceId, posts2.Select(p => p.Id).ToList(), memberships.Where(m => posts2.Any(p => p.Id == m.PostId)).Select(m => m.CollectionId), !on, now, ct));
                break;
            }
            case BulkPostAction.Delete:
                affected.AddRange(await sync.ChangedAsync(c.WorkspaceId, found.Select(p => p.Id).ToList(), memberships.Select(m => m.CollectionId), true, now, ct));
                posts.RemoveRange(found);
                changed = found.Count;
                break;
            case BulkPostAction.AddToCollection:
            {
                var collection = await collections.GetAsync(c.WorkspaceId, c.CollectionId ?? Guid.Empty, ct) ?? throw new DomainException("ไม่พบชุดโพสต์ที่เลือก");
                var perPost = memberships.GroupBy(m => m.PostId).ToDictionary(g => g.Key, g => g.Select(m => m.CollectionId).ToHashSet());
                var adding = found.Where(p => !perPost.GetValueOrDefault(p.Id, []).Contains(collection.Id)).ToList();
                if (adding.Any(p => perPost.GetValueOrDefault(p.Id, []).Count >= CollectionPost.MaxCollections))
                    throw new DomainException($"โพสต์หนึ่งอยู่ได้ไม่เกิน {CollectionPost.MaxCollections} ชุดโพสต์");
                posts.AddMembers(adding.Select(p => CollectionMember.Create(c.WorkspaceId, collection.Id, p.Id, now)));
                changed = adding.Count;
                affected.AddRange(await sync.ChangedAsync(c.WorkspaceId, adding.Select(p => p.Id).ToList(), [collection.Id], false, now, ct));
                break;
            }
            default:
            {
                var collectionId = c.CollectionId ?? throw new DomainException("ไม่พบชุดโพสต์ที่เลือก");
                var leaving = (await posts.ListMembersOfCollectionsAsync(c.WorkspaceId, [collectionId], ct)).Where(m => ids.Contains(m.PostId)).ToList();
                posts.RemoveMembers(leaving);
                changed = leaving.Count;
                affected.AddRange(await sync.ChangedAsync(c.WorkspaceId, leaving.Select(m => m.PostId).ToList(), [collectionId], true, now, ct));
                break;
            }
        }
        await uow.SaveChangesAsync(ct);
        await SourceTopUp.RunAsync(topUp, c.WorkspaceId, affected.DistinctBy(s => s.Id).ToList(), ct);
        return new BulkMasterPostsResultDto(changed);
    }
}
