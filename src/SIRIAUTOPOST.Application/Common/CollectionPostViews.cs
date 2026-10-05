using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>Builds the DTOs of master posts: their collections and how much they made, read from the database in a few queries.</summary>
public sealed class CollectionPostViews(ICollectionPostRepository posts, IPostRepository postRepo, TimeProvider clock)
{
    /// <summary>Every master post of the workspace, oldest first.</summary>
    public async Task<IReadOnlyList<CollectionPostDto>> ListAsync(Guid workspaceId, CancellationToken ct) =>
        await BuildAsync(workspaceId, await posts.ListAsync(workspaceId, ct), ct);

    /// <summary>The posts of one collection, oldest first.</summary>
    public async Task<IReadOnlyList<CollectionPostDto>> ForCollectionAsync(Guid workspaceId, Guid collectionId, CancellationToken ct) =>
        await BuildAsync(workspaceId, await posts.ListByCollectionAsync(workspaceId, collectionId, ct), ct);

    public async Task<CollectionPostDto> OneAsync(CollectionPost post, CancellationToken ct) =>
        (await BuildAsync(post.WorkspaceId, [post], ct))[0];

    public async Task<IReadOnlyList<CollectionPostDto>> BuildAsync(Guid workspaceId, IReadOnlyList<CollectionPost> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var members = (await posts.ListMembersAsync(workspaceId, ct)).ToLookup(m => m.PostId);
        var usage = await postRepo.UsageByCollectionPostAsync(workspaceId, clock.GetUtcNow(), ct);
        return list.Select(p => CollectionPostDto.From(
            p, members[p.Id].OrderBy(m => m.AddedAt).ThenBy(m => m.CollectionId).Select(m => m.CollectionId).ToList(),
            usage.GetValueOrDefault(p.Id))).ToList();
    }
}

/// <summary>
/// What a change to a master post does to the schedules that use its collections: the queued posts it already made for the
/// future go (their text or the post itself is no longer right), and the schedules look at their horizon again to make
/// what is missing. Nothing is saved here: the caller saves, then lets <see cref="SourceTopUp"/> refill the schedules.
/// </summary>
public sealed class CollectionPostSync(IScheduleRepository schedules, IPostRepository posts)
{
    public async Task<IReadOnlyList<Schedule>> ChangedAsync(
        Guid workspaceId, IReadOnlyCollection<Guid> postIds, IEnumerable<Guid> collectionIds, bool dropQueued, DateTimeOffset now, CancellationToken ct)
    {
        var affected = new Dictionary<Guid, Schedule>();
        foreach (var id in collectionIds.Distinct())
            foreach (var s in await schedules.ListByCollectionAsync(workspaceId, id, ct))
                affected[s.Id] = s;
        if (affected.Count == 0 || postIds.Count == 0) return [.. affected.Values];
        if (dropQueued)
            posts.RemoveRange((await posts.ListFutureQueuedByCollectionPostAsync(postIds, now, ct))
                .Where(p => p.ScheduleId is { } sid && affected.ContainsKey(sid)));
        foreach (var s in affected.Values)
            if (s.Active) s.InvalidateGenerated();
        return [.. affected.Values];
    }
}

/// <summary>Shared lookups of the master post handlers.</summary>
internal static class MasterPostLookups
{
    public static async Task<CollectionPost> RequireAsync(ICollectionPostRepository posts, Guid workspaceId, Guid id, CancellationToken ct) =>
        await posts.GetAsync(workspaceId, id, ct) ?? throw new NotFoundException("โพสต์", id);

    /// <summary>The collections of a workspace by id; one that is missing is a 422 (a stale form), not a 404.</summary>
    public static async Task<IReadOnlyList<PostCollection>> RequireCollectionsAsync(
        ICollectionRepository collections, Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count > CollectionPost.MaxCollections)
            throw new DomainException($"โพสต์หนึ่งอยู่ได้ไม่เกิน {CollectionPost.MaxCollections} ชุดโพสต์");
        if (wanted.Count == 0) return [];
        var found = (await collections.ListAsync(workspaceId, ct)).Where(c => wanted.Contains(c.Id)).ToList();
        if (found.Count != wanted.Count) throw new DomainException("ไม่พบชุดโพสต์บางชุดที่เลือก");
        return found;
    }
}
