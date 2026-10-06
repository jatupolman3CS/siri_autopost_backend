using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Collections;

// Collections ("ชุดโพสต์"): named sets of master posts with composing settings and an optional approval flow. A master
// post (the library, Features/MasterPosts) may sit in several collections. Viewers read, editors write, admins approve.

/// <summary>Shared lookups of the collection handlers.</summary>
internal static class CollectionLookups
{
    public static async Task<PostCollection> RequireAsync(ICollectionRepository collections, Guid workspaceId, Guid id, CancellationToken ct) =>
        await collections.GetAsync(workspaceId, id, ct) ?? throw new NotFoundException("ชุดโพสต์", id);

    public static async Task EnsureMediaExistAsync(IMediaRepository media, Guid workspaceId, IEnumerable<Guid>? ids, CancellationToken ct)
    {
        var list = (ids ?? []).Distinct().ToList();
        if (list.Count > 0 && await media.CountExistingAsync(workspaceId, list, ct) != list.Count)
            throw new DomainException("ไม่พบไฟล์สื่อบางรายการในคลัง");
    }

    public static async Task EnsureRoomForPostsAsync(ICollectionPostRepository posts, Guid workspaceId, int adding, CancellationToken ct)
    {
        if (await posts.CountAsync(workspaceId, ct) + adding > CollectionPost.MaxPerWorkspace)
            throw new DomainException($"เก็บโพสต์ในคลังโพสต์ได้ไม่เกิน {CollectionPost.MaxPerWorkspace:N0} โพสต์ต่อเวิร์กสเปซ");
    }

    public static async Task<CollectionDto> ViewAsync(
        PostCollection c, CollectionPostViews views, IScheduleRepository schedules, CancellationToken ct)
    {
        var list = await views.ForCollectionAsync(c.WorkspaceId, c.Id, ct);
        var scheduleCount = (await schedules.ListByCollectionAsync(c.WorkspaceId, c.Id, ct)).Count;
        return CollectionDto.From(c, list, scheduleCount);
    }

    /// <summary>The post as a member of this collection, or a 404 (a post of the library that is not in it is not "in the collection").</summary>
    public static async Task<(CollectionPost Post, CollectionMember Member)> RequireMemberAsync(
        ICollectionPostRepository posts, Guid workspaceId, Guid collectionId, Guid postId, CancellationToken ct)
    {
        var post = await posts.GetAsync(workspaceId, postId, ct);
        var member = post is null ? null : (await posts.ListMembersOfPostAsync(workspaceId, postId, ct)).FirstOrDefault(m => m.CollectionId == collectionId);
        if (post is null || member is null) throw new NotFoundException("โพสต์ในชุดโพสต์", postId);
        return (post, member);
    }

    /// <summary>Does any collection the post sits in require approval? Then a change of its content sends it back to draft.</summary>
    public static async Task<bool> AnyRequiresApprovalAsync(
        ICollectionRepository collections, ICollectionPostRepository posts, Guid workspaceId, Guid postId, CancellationToken ct)
    {
        var ids = (await posts.ListMembersOfPostAsync(workspaceId, postId, ct)).Select(m => m.CollectionId).ToHashSet();
        return ids.Count > 0 && (await collections.ListAsync(workspaceId, ct)).Any(c => ids.Contains(c.Id) && c.Settings.RequireApproval);
    }
}

public sealed record GetCollectionsQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<CollectionDto>>;

/// <summary>Every collection with its posts (oldest first), how often each was posted and how many schedules use it.</summary>
public sealed class GetCollectionsQueryHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, CollectionPostViews views, IScheduleRepository schedules, ICurrentUser current)
    : IQueryHandler<GetCollectionsQuery, IReadOnlyList<CollectionDto>>
{
    public async Task<IReadOnlyList<CollectionDto>> HandleAsync(GetCollectionsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var all = await collections.ListAsync(q.WorkspaceId, ct);
        var posts = await views.ListAsync(q.WorkspaceId, ct);
        var used = (await schedules.ListAsync(q.WorkspaceId, ct)).ToLookup(s => s.CollectionId);
        return all.Select(c => CollectionDto.From(c, posts.Where(p => p.CollectionIds.Contains(c.Id)).ToList(), used[c.Id].Count())).ToList();
    }
}

public sealed record CreateCollectionCommand(Guid WorkspaceId, string Name, string? Description) : ICommand<CollectionDto>;

public sealed class CreateCollectionCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, CollectionPostViews views, IScheduleRepository schedules,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateCollectionCommand, CollectionDto>
{
    public async Task<CollectionDto> HandleAsync(CreateCollectionCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        if (await collections.CountAsync(c.WorkspaceId, ct) >= PostCollection.MaxPerWorkspace)
            throw new DomainException($"สร้างชุดโพสต์ได้ไม่เกิน {PostCollection.MaxPerWorkspace} ชุดต่อเวิร์กสเปซ");
        var collection = PostCollection.Create(c.WorkspaceId, c.Name, c.Description, clock.GetUtcNow(), await collections.MaxSortOrderAsync(c.WorkspaceId, ct) + 1);
        collections.Add(collection);
        await uow.SaveChangesAsync(ct);
        return await CollectionLookups.ViewAsync(collection, views, schedules, ct);
    }
}

/// <summary>
/// Editors change a collection's name and composing settings, but turning <c>requireApproval</c> on or off is an admin's
/// switch (403 for an editor who changes it): the approval step is only worth something if the people it checks cannot
/// switch it off.
/// </summary>
public sealed record UpdateCollectionCommand(
    Guid WorkspaceId, Guid CollectionId, string Name, string? Description, string? Icon, CollectionSettingsDto Settings) : ICommand<CollectionDto>;

public sealed class UpdateCollectionCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, CollectionPostViews views, IScheduleRepository schedules,
    ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<UpdateCollectionCommand, CollectionDto>
{
    public async Task<CollectionDto> HandleAsync(UpdateCollectionCommand c, CancellationToken ct = default)
    {
        var (_, role) = await workspaces.RequireRoleAsync(c.WorkspaceId, current, ct);
        if (role < WorkspaceRole.Editor) throw new ForbiddenException("สิทธิ์ของคุณในเวิร์กสเปซนี้ทำรายการนี้ไม่ได้");
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        if (c.Settings.RequireApproval != collection.Settings.RequireApproval && role < WorkspaceRole.Admin)
            throw new ForbiddenException("การเปิดหรือปิดการอนุมัติโพสต์ต้องเป็นผู้ดูแล (admin) ขึ้นไป");
        collection.Update(c.Name, c.Description, c.Icon, c.Settings.ToSettings());
        await uow.SaveChangesAsync(ct);
        return await CollectionLookups.ViewAsync(collection, views, schedules, ct);
    }
}

/// <summary>
/// Switches a collection on or off. Off: the schedules that use it queue nothing from it and the posts they still had queued
/// for the future are removed. On: those schedules fill in what is missing.
/// </summary>
public sealed record SetCollectionActiveCommand(Guid WorkspaceId, Guid CollectionId, bool Active) : ICommand<CollectionDto>;

public sealed class SetCollectionActiveCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, CollectionPostViews views, IScheduleRepository schedules,
    ScheduleSync sync, ScheduleTopUp topUp, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetCollectionActiveCommand, CollectionDto>
{
    public async Task<CollectionDto> HandleAsync(SetCollectionActiveCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        if (collection.Active != c.Active)
        {
            var used = await schedules.ListByCollectionAsync(c.WorkspaceId, c.CollectionId, ct);
            collection.SetActive(c.Active);
            await sync.SourceToggledAsync(used, c.Active, clock.GetUtcNow(), ct);
            await uow.SaveChangesAsync(ct);
            if (c.Active) await SourceTopUp.RunAsync(topUp, c.WorkspaceId, used, ct);
        }
        return await CollectionLookups.ViewAsync(collection, views, schedules, ct);
    }
}

/// <summary>Deletes a collection (its posts stay in the library). Refused while a schedule uses it.</summary>
public sealed record DeleteCollectionCommand(Guid WorkspaceId, Guid CollectionId) : ICommand<Unit>;

public sealed class DeleteCollectionCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, IScheduleRepository schedules, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeleteCollectionCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteCollectionCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        var used = await schedules.ListByCollectionAsync(c.WorkspaceId, c.CollectionId, ct);
        if (used.Count > 0)
            throw new DomainException($"ลบชุดโพสต์นี้ไม่ได้ เพราะยังใช้อยู่ในตาราง: {string.Join(", ", used.Select(s => s.Name))}");
        collections.Remove(collection);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Writes a new post straight into a collection: it joins the library and sits in this collection.</summary>
public sealed record AddCollectionPostCommand(Guid WorkspaceId, Guid CollectionId, string Text, IReadOnlyList<Guid>? MediaIds)
    : ICommand<CollectionPostDto>;

public sealed class AddCollectionPostCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, IMediaRepository media,
    CollectionPostViews views, PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<AddCollectionPostCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(AddCollectionPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        var now = clock.GetUtcNow();
        var post = CollectionPost.Create(collection, c.Text, c.MediaIds, now);
        await CollectionLookups.EnsureMediaExistAsync(media, c.WorkspaceId, post.MediaIds, ct);
        await CollectionLookups.EnsureRoomForPostsAsync(posts, c.WorkspaceId, 1, ct);
        await quotas.EnsureLibraryPostsAsync(c.WorkspaceId, 1, ct);
        posts.Add(post);
        posts.AddMember(CollectionMember.Create(c.WorkspaceId, collection.Id, post.Id, now));
        await uow.SaveChangesAsync(ct);
        return await views.OneAsync(post, ct);
    }
}

/// <summary>Several posts at once (the AI writer), all or none.</summary>
public sealed record AddCollectionPostsBatchCommand(Guid WorkspaceId, Guid CollectionId, IReadOnlyList<CollectionPostInput> Items)
    : ICommand<IReadOnlyList<CollectionPostDto>>;

public sealed class AddCollectionPostsBatchCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, IMediaRepository media,
    CollectionPostViews views, PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<AddCollectionPostsBatchCommand, IReadOnlyList<CollectionPostDto>>
{
    public const int MaxItems = 20;

    public async Task<IReadOnlyList<CollectionPostDto>> HandleAsync(AddCollectionPostsBatchCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        var now = clock.GetUtcNow();
        var created = c.Items.Select(i => CollectionPost.Create(collection, i.Text, i.MediaIds, now)).ToList();
        await CollectionLookups.EnsureMediaExistAsync(media, c.WorkspaceId, created.SelectMany(p => p.MediaIds), ct);
        await CollectionLookups.EnsureRoomForPostsAsync(posts, c.WorkspaceId, created.Count, ct);
        await quotas.EnsureLibraryPostsAsync(c.WorkspaceId, created.Count, ct);
        posts.AddRange(created);
        posts.AddMembers(created.Select(p => CollectionMember.Create(c.WorkspaceId, collection.Id, p.Id, now)));
        await uow.SaveChangesAsync(ct);
        return await views.BuildAsync(c.WorkspaceId, created, ct);
    }
}

/// <summary>Puts posts of the library into a collection (the ones already in it stay as they are).</summary>
public sealed record AddPostsToCollectionCommand(Guid WorkspaceId, Guid CollectionId, IReadOnlyList<Guid> PostIds)
    : ICommand<CollectionDto>;

public sealed class AddPostsToCollectionCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, CollectionPostViews views,
    IScheduleRepository schedules, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<AddPostsToCollectionCommand, CollectionDto>
{
    public const int MaxItems = 500;

    public async Task<CollectionDto> HandleAsync(AddPostsToCollectionCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        var wanted = c.PostIds.Distinct().ToList();
        var found = await posts.ListByIdsAsync(c.WorkspaceId, wanted, ct);
        if (found.Count != wanted.Count) throw new DomainException("ไม่พบโพสต์บางรายการในคลังโพสต์");
        var already = (await posts.ListMembersOfCollectionsAsync(c.WorkspaceId, [collection.Id], ct)).Select(m => m.PostId).ToHashSet();
        var now = clock.GetUtcNow();
        // A post may sit in at most MaxCollections collections.
        var counts = (await posts.ListMembersAsync(c.WorkspaceId, ct)).GroupBy(m => m.PostId).ToDictionary(g => g.Key, g => g.Count());
        var adding = found.Where(p => !already.Contains(p.Id)).ToList();
        if (adding.Any(p => counts.GetValueOrDefault(p.Id) >= CollectionPost.MaxCollections))
            throw new DomainException($"โพสต์หนึ่งอยู่ได้ไม่เกิน {CollectionPost.MaxCollections} ชุดโพสต์");
        posts.AddMembers(adding.Select(p => CollectionMember.Create(c.WorkspaceId, collection.Id, p.Id, now)));
        await uow.SaveChangesAsync(ct);
        return await CollectionLookups.ViewAsync(collection, views, schedules, ct);
    }
}

/// <summary>
/// Changes a post's text and media from the collection's screen, and moves it to another collection when
/// <paramref name="TargetCollectionId"/> says so (it leaves this one and joins the other).
/// </summary>
public sealed record UpdateCollectionPostCommand(
    Guid WorkspaceId, Guid CollectionId, Guid PostId, string Text, IReadOnlyList<Guid>? MediaIds, Guid? TargetCollectionId)
    : ICommand<CollectionPostDto>;

public sealed class UpdateCollectionPostCommandHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository posts, IMediaRepository media,
    CollectionPostViews views, CollectionPostSync sync, ScheduleTopUp topUp, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateCollectionPostCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(UpdateCollectionPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, c.CollectionId, ct);
        var (post, member) = await CollectionLookups.RequireMemberAsync(posts, c.WorkspaceId, collection.Id, c.PostId, ct);
        await CollectionLookups.EnsureMediaExistAsync(media, c.WorkspaceId, c.MediaIds, ct);
        var now = clock.GetUtcNow();
        var memberIds = (await posts.ListMembersOfPostAsync(c.WorkspaceId, post.Id, ct)).Select(m => m.CollectionId).ToList();
        var requireApproval = await CollectionLookups.AnyRequiresApprovalAsync(collections, posts, c.WorkspaceId, post.Id, ct);
        var before = (post.Text, post.MediaIds.ToList());
        post.Edit(c.Text, c.MediaIds, requireApproval, now);
        var changed = before.Text != post.Text || !before.Item2.SequenceEqual(post.MediaIds);

        var affected = changed ? memberIds : [];
        if (c.TargetCollectionId is { } target && target != collection.Id)
        {
            var targetCollection = await CollectionLookups.RequireAsync(collections, c.WorkspaceId, target, ct);
            posts.RemoveMembers([member]);
            if (!memberIds.Contains(targetCollection.Id)) posts.AddMember(CollectionMember.Create(c.WorkspaceId, targetCollection.Id, post.Id, now));
            affected = [.. affected, collection.Id];
        }
        var schedules = await sync.ChangedAsync(c.WorkspaceId, [post.Id], affected, dropQueued: true, now, ct);
        await uow.SaveChangesAsync(ct);
        await SourceTopUp.RunAsync(topUp, c.WorkspaceId, schedules, ct);
        return await views.OneAsync(post, ct);
    }
}

/// <summary>Takes a post out of a collection. The post itself stays in the library.</summary>
public sealed record DeleteCollectionPostCommand(Guid WorkspaceId, Guid CollectionId, Guid PostId) : ICommand<Unit>;

public sealed class DeleteCollectionPostCommandHandler(
    IWorkspaceRepository workspaces, ICollectionPostRepository posts, CollectionPostSync sync, ScheduleTopUp topUp, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeleteCollectionPostCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteCollectionPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var (post, member) = await CollectionLookups.RequireMemberAsync(posts, c.WorkspaceId, c.CollectionId, c.PostId, ct);
        posts.RemoveMembers([member]);
        var schedules = await sync.ChangedAsync(c.WorkspaceId, [post.Id], [c.CollectionId], dropQueued: true, clock.GetUtcNow(), ct);
        await uow.SaveChangesAsync(ct);
        await SourceTopUp.RunAsync(topUp, c.WorkspaceId, schedules, ct);
        return Unit.Value;
    }
}

/// <summary>An editor asks for approval; an admin approves or sends the post back to draft.</summary>
public sealed record CollectionPostApprovalCommand(Guid WorkspaceId, Guid CollectionId, Guid PostId, ApprovalAction Action)
    : ICommand<CollectionPostDto>;

public sealed class CollectionPostApprovalCommandHandler(
    IWorkspaceRepository workspaces, ICollectionPostRepository posts, CollectionPostViews views, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CollectionPostApprovalCommand, CollectionPostDto>
{
    public async Task<CollectionPostDto> HandleAsync(CollectionPostApprovalCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, c.Action == ApprovalAction.Request ? WorkspaceRole.Editor : WorkspaceRole.Admin, ct);
        var (post, _) = await CollectionLookups.RequireMemberAsync(posts, c.WorkspaceId, c.CollectionId, c.PostId, ct);
        var now = clock.GetUtcNow();
        switch (c.Action)
        {
            case ApprovalAction.Request: post.RequestApproval(now); break;
            case ApprovalAction.Approve: post.Approve(now); break;
            default: post.Reject(now); break;
        }
        await uow.SaveChangesAsync(ct);
        return await views.OneAsync(post, ct);
    }
}
