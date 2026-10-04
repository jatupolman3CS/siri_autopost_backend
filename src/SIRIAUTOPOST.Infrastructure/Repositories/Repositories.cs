using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Repositories;

public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail, ct);

    public Task<User?> GetByStripeCustomerAsync(string customerId, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(x => x.StripeCustomerId == customerId, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default) =>
        await db.Users.OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<User>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.Where(x => list.Contains(x.Id)).ToListAsync(ct);
    }

    public void Add(User user) => db.Users.Add(user);
}

public sealed class WorkspaceRepository(AppDbContext db) : IWorkspaceRepository
{
    public Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Workspaces.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<Workspace>> ListByOwnerAsync(Guid ownerId, CancellationToken ct = default) =>
        await db.Workspaces.Where(x => x.OwnerId == ownerId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Workspace>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return await db.Workspaces.Where(x => list.Contains(x.Id)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Workspace>> ListAllAsync(CancellationToken ct = default) =>
        await db.Workspaces.OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public async Task<WorkspaceRole?> GetMemberRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct = default) =>
        await db.Members.Where(x => x.WorkspaceId == workspaceId && x.UserId == userId)
            .Select(x => (WorkspaceRole?)x.Role).FirstOrDefaultAsync(ct);

    public void Add(Workspace workspace) => db.Workspaces.Add(workspace);
}

public sealed class AccountRepository(AppDbContext db) : IAccountRepository
{
    public async Task<IReadOnlyList<SocialAccount>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Accounts.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);

    public Task<SocialAccount?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Accounts.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<SocialAccount?> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default) =>
        db.Accounts.FirstOrDefaultAsync(x => x.DeviceId == deviceId, ct);

    public Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.Accounts.CountAsync(x => x.WorkspaceId == workspaceId, ct);

    public Task<int> CountConnectedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return db.Accounts.CountAsync(x => ids.Contains(x.WorkspaceId) && x.DeviceId != null, ct);
    }

    public async Task<IReadOnlyList<SocialAccount>> ListConnectedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return await db.Accounts.Where(x => ids.Contains(x.WorkspaceId) && x.DeviceId != null).ToListAsync(ct);
    }

    public void Add(SocialAccount account) => db.Accounts.Add(account);
}

public sealed class PostRepository(AppDbContext db) : IPostRepository
{
    public Task<Post?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Posts.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public async Task<IReadOnlyList<Post>> ListAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        await db.Posts.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.ScheduledAt >= from && x.ScheduledAt < to)
            .OrderBy(x => x.ScheduledAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Post>> ListOpenErrorsAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Posts.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && !x.ErrorDismissed &&
                        (x.Status == PostStatus.Failed || x.Status == PostStatus.Pending))
            .OrderByDescending(x => x.ScheduledAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Post>> ListByStatusAsync(Guid workspaceId, PostStatus status, CancellationToken ct = default) =>
        await db.Posts.Where(x => x.WorkspaceId == workspaceId && x.Status == status).OrderBy(x => x.ScheduledAt).ToListAsync(ct);

    public async Task<Dictionary<Guid, int>> CountSentSinceAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset since, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return await db.Posts
            .Where(x => ids.Contains(x.WorkspaceId) && x.Status == PostStatus.Success && x.ScheduledAt >= since)
            .GroupBy(x => x.WorkspaceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<Post>> ListDueAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default) =>
        await db.Posts
            .Where(x => x.AccountId == accountId && x.Status == PostStatus.Queued && x.ScheduledAt <= now)
            .OrderBy(x => x.ScheduledAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Post>> ListOpenByAccountAsync(Guid accountId, CancellationToken ct = default) =>
        await db.Posts.Where(x => x.AccountId == accountId && (x.Status == PostStatus.Queued || x.Status == PostStatus.Waiting)).ToListAsync(ct);

    public async Task<IReadOnlyList<Post>> ListClaimedByAsync(Guid deviceId, CancellationToken ct = default) =>
        await db.Posts.Where(x => x.ClaimedByDeviceId == deviceId && x.Status == PostStatus.Posting).ToListAsync(ct);

    // Posts of accounts connected through a paired browser (the sample accounts' history never went out).
    private IQueryable<Post> Real => db.Posts.Where(p => db.Accounts.Any(a => a.Id == p.AccountId && a.DeviceId != null));

    public Task<int> CountPublishedSinceAsync(Guid workspaceId, Platform platform, DateTimeOffset since, CancellationToken ct = default) =>
        Real.CountAsync(x => x.WorkspaceId == workspaceId && x.Platform == platform &&
                             x.PublishedAt != null && x.PublishedAt >= since, ct);

    public Task<int> CountPublishedSinceAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset since, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return Real.CountAsync(x => ids.Contains(x.WorkspaceId) && x.PublishedAt != null && x.PublishedAt >= since, ct);
    }

    public async Task<IReadOnlyList<(Guid WorkspaceId, PostStatus Status, int Count)>> CountByStatusAsync(
        IEnumerable<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        var rows = await Real.Where(x => ids.Contains(x.WorkspaceId) && x.ScheduledAt >= from && x.ScheduledAt < to)
            .GroupBy(x => new { x.WorkspaceId, x.Status })
            .Select(g => new { g.Key.WorkspaceId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => (r.WorkspaceId, r.Status, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<Post>> ListRecentAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset before, int take, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return await Real.AsNoTracking().Where(x => ids.Contains(x.WorkspaceId) && x.ScheduledAt < before)
            .OrderByDescending(x => x.ScheduledAt).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Post>> ListFailedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return await Real.Where(x => ids.Contains(x.WorkspaceId) && x.Status == PostStatus.Failed && !x.ErrorDismissed).ToListAsync(ct);
    }

    public Task<DateTimeOffset?> LastPublishedAtAsync(Guid accountId, CancellationToken ct = default) =>
        db.Posts.Where(x => x.AccountId == accountId && x.PublishedAt != null).MaxAsync(x => x.PublishedAt, ct);

    public void Add(Post post) => db.Posts.Add(post);

    public void Remove(Post post) => db.Posts.Remove(post);

    public void RemoveRange(IEnumerable<Post> posts) => db.Posts.RemoveRange(posts);

    // ---- schedules ----

    public async Task<IReadOnlyList<Post>> ListFutureQueuedByScheduleAsync(Guid scheduleId, DateTimeOffset now, CancellationToken ct = default) =>
        await db.Posts.Where(x => x.ScheduleId == scheduleId && x.Status == PostStatus.Queued && x.ScheduledAt > now).ToListAsync(ct);

    public async Task<IReadOnlyList<(string TargetKey, string SlotKey)>> ListScheduleKeysAsync(
        Guid scheduleId, DateTimeOffset scheduledFrom, CancellationToken ct = default)
    {
        var rows = await db.Posts.AsNoTracking()
            .Where(x => x.ScheduleId == scheduleId && x.ScheduledAt >= scheduledFrom)
            .Select(x => new { x.TargetKey, x.SlotKey })
            .ToListAsync(ct);
        return rows.Select(r => (r.TargetKey ?? "", r.SlotKey ?? "")).ToList();
    }

    public async Task<Dictionary<Guid, int>> CountByScheduleAsync(
        IEnumerable<Guid> scheduleIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var ids = scheduleIds.Distinct().ToList();
        return await db.Posts
            .Where(x => x.ScheduleId != null && ids.Contains(x.ScheduleId.Value) && x.ScheduledAt >= from && x.ScheduledAt < to)
            .GroupBy(x => x.ScheduleId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public async Task<Dictionary<Guid, DateTimeOffset>> NextQueuedAtByScheduleAsync(
        IEnumerable<Guid> scheduleIds, DateTimeOffset now, CancellationToken ct = default)
    {
        var ids = scheduleIds.Distinct().ToList();
        return await db.Posts
            .Where(x => x.ScheduleId != null && ids.Contains(x.ScheduleId.Value) && x.Status == PostStatus.Queued && x.ScheduledAt > now)
            .GroupBy(x => x.ScheduleId!.Value)
            .Select(g => new { g.Key, At = g.Min(p => p.ScheduledAt) })
            .ToDictionaryAsync(x => x.Key, x => x.At, ct);
    }

    public async Task<IReadOnlyList<Post>> ListBySlotAsync(Guid scheduleId, string slotKey, CancellationToken ct = default) =>
        await db.Posts.AsNoTracking().Where(x => x.ScheduleId == scheduleId && x.SlotKey == slotKey).OrderBy(x => x.ScheduledAt).ToListAsync(ct);

    public Task<int> CountOpenInSlotAsync(Guid scheduleId, string slotKey, CancellationToken ct = default) =>
        db.Posts.CountAsync(x => x.ScheduleId == scheduleId && x.SlotKey == slotKey &&
                                 (x.Status == PostStatus.Queued || x.Status == PostStatus.Waiting || x.Status == PostStatus.Posting), ct);

    // ---- links ----

    public Task<int> CountPublishedToLinkSinceAsync(Guid linkId, DateTimeOffset since, CancellationToken ct = default) =>
        Real.CountAsync(x => x.LinkId == linkId && x.PublishedAt != null && x.PublishedAt >= since, ct);

    public async Task<Dictionary<Guid, int>> CountPublishedToLinksSinceAsync(IEnumerable<Guid> linkIds, DateTimeOffset since, CancellationToken ct = default)
    {
        var ids = linkIds.Distinct().ToList();
        return await Real
            .Where(x => x.LinkId != null && ids.Contains(x.LinkId.Value) && x.PublishedAt != null && x.PublishedAt >= since)
            .GroupBy(x => x.LinkId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    public Task<DateTimeOffset?> LastPublishedToLinkAtAsync(Guid linkId, CancellationToken ct = default) =>
        db.Posts.Where(x => x.LinkId == linkId && x.PublishedAt != null).MaxAsync(x => x.PublishedAt, ct);

    private sealed record RecentUse(Guid LinkId, Guid CollectionPostId);

    public async Task<Dictionary<Guid, IReadOnlyList<Guid>>> ListRecentCollectionPostIdsByLinkAsync(
        IEnumerable<Guid> linkIds, int take, CancellationToken ct = default)
    {
        var ids = linkIds.Distinct().ToArray();
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        if (ids.Length == 0 || take <= 0) return result;
        // The newest N per link in one query. Statuses are stored by name.
        var rows = await db.Database.SqlQuery<RecentUse>($"""
            SELECT t.link_id AS "LinkId", t.collection_post_id AS "CollectionPostId"
            FROM (
                SELECT p.link_id, p.collection_post_id,
                       row_number() OVER (PARTITION BY p.link_id ORDER BY p.scheduled_at DESC, p.id) AS rn
                FROM "POSTS" p
                WHERE p.link_id = ANY({ids}) AND p.collection_post_id IS NOT NULL
                  AND p.status IN ('Queued', 'Waiting', 'Posting', 'Success', 'Pending')
            ) t
            WHERE t.rn <= {take}
            ORDER BY t.link_id, t.rn
            """).ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => r.LinkId)) result[g.Key] = g.Select(r => r.CollectionPostId).ToList();
        return result;
    }

    // ---- limits and health ----

    public Task<int> CountPublishedInWorkspaceSinceAsync(Guid workspaceId, DateTimeOffset since, CancellationToken ct = default) =>
        Real.CountAsync(x => x.WorkspaceId == workspaceId && x.PublishedAt != null && x.PublishedAt >= since, ct);

    public async Task<(int Finished, int Failed)> CountOutcomesSinceAsync(Guid workspaceId, DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = await Real
            .Where(x => x.WorkspaceId == workspaceId && !x.IsTest && x.UpdatedAt >= since &&
                        (x.Status == PostStatus.Success || x.Status == PostStatus.Pending || x.Status == PostStatus.Failed))
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return (rows.Sum(r => r.Count), rows.Where(r => r.Status == PostStatus.Failed).Sum(r => r.Count));
    }

    public async Task<IReadOnlyList<PostStatus>> ListRecentOutcomesAsync(Guid accountId, int take, CancellationToken ct = default) =>
        await db.Posts.AsNoTracking()
            .Where(x => x.AccountId == accountId &&
                        (x.Status == PostStatus.Success || x.Status == PostStatus.Pending || x.Status == PostStatus.Failed))
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => x.Status)
            .Take(take)
            .ToListAsync(ct);

    // ---- reports ----

    public async Task<IReadOnlyList<PostOutcome>> ListOutcomesAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        await Real.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && !x.IsTest && x.ScheduledAt >= from && x.ScheduledAt < to &&
                        (x.Status == PostStatus.Success || x.Status == PostStatus.Pending || x.Status == PostStatus.Failed))
            .Select(x => new PostOutcome(x.Id, x.AccountId, x.Platform, x.LinkId, x.Target, x.TargetUrl, x.CollectionPostId, x.Status, x.ScheduledAt, x.PublishedAt))
            .ToListAsync(ct);

    public async Task<Dictionary<Guid, int>> CountPostedByCollectionPostAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Posts
            .Where(x => x.WorkspaceId == workspaceId && x.CollectionPostId != null && x.Status == PostStatus.Success && !x.IsTest)
            .GroupBy(x => x.CollectionPostId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task<IReadOnlyDictionary<int, int>> CountPublishedByHourAsync(
        Guid workspaceId, DateTimeOffset since, int utcOffsetMinutes, CancellationToken ct = default)
    {
        var times = await Real.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && !x.IsTest && x.PublishedAt != null && x.PublishedAt >= since)
            .Select(x => x.PublishedAt!.Value)
            .ToListAsync(ct);
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        return times.GroupBy(t => t.ToOffset(offset).Hour).ToDictionary(g => g.Key, g => g.Count());
    }
}

public sealed class MediaRepository(AppDbContext db) : IMediaRepository
{
    // The list never loads file bytes.
    public async Task<IReadOnlyList<MediaSummary>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Media.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new MediaSummary(x.Id, x.Name, x.ContentType, x.Kind, x.Size, x.UsedCount, x.CreatedAt))
            .ToListAsync(ct);

    public Task<MediaFile?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<int> CountExistingAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return db.Media.CountAsync(x => x.WorkspaceId == workspaceId && list.Contains(x.Id), ct);
    }

    public Task RecordUseAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return db.Media.Where(x => x.WorkspaceId == workspaceId && list.Contains(x.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedCount, x => x.UsedCount + 1), ct);
    }

    public void Add(MediaFile file) => db.Media.Add(file);
}

public sealed class SnippetRepository(AppDbContext db) : ISnippetRepository
{
    public async Task<IReadOnlyList<Snippet>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Snippets.AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

    public void Add(Snippet snippet) => db.Snippets.Add(snippet);
}

public sealed class DeviceRepository(AppDbContext db) : IDeviceRepository
{
    public async Task<IReadOnlyList<Device>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Devices.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public Task<Device?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Devices.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Devices.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<Device?> GetByKeyHashAsync(string keyHash, CancellationToken ct = default) =>
        db.Devices.FirstOrDefaultAsync(x => x.KeyHash == keyHash, ct);

    public Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.Devices.CountAsync(x => x.WorkspaceId == workspaceId, ct);

    public async Task<IReadOnlyList<Device>> ListByWorkspacesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return await db.Devices.Where(x => ids.Contains(x.WorkspaceId)).ToListAsync(ct);
    }

    public void Add(Device device) => db.Devices.Add(device);

    public void Remove(Device device) => db.Devices.Remove(device);
}

public sealed class DevicePairingRepository(AppDbContext db) : IDevicePairingRepository
{
    public Task<DevicePairing?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        db.DevicePairings.FirstOrDefaultAsync(x => x.Code == code, ct);

    public Task DeleteExpiredAsync(DateTimeOffset before, CancellationToken ct = default) =>
        db.DevicePairings.Where(x => x.ExpiresAt < before).ExecuteDeleteAsync(ct);

    public void Add(DevicePairing pairing) => db.DevicePairings.Add(pairing);
}

public sealed class ExtensionRepository(AppDbContext db) : IExtensionRepository
{
    public Task<ExtensionConfig?> GetConfigAsync(Guid deviceId, CancellationToken ct = default) =>
        db.ExtensionConfigs.FirstOrDefaultAsync(x => x.DeviceId == deviceId, ct);

    public async Task<(int Revision, bool HasContent)?> GetConfigHeadAsync(Guid deviceId, CancellationToken ct = default)
    {
        var head = await db.ExtensionConfigs.Where(x => x.DeviceId == deviceId)
            .Select(x => new { x.Revision, x.HasContent }).FirstOrDefaultAsync(ct);
        return head is null ? null : (head.Revision, head.HasContent);
    }

    public async Task<IReadOnlyList<string>> ListSettingsAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.ExtensionConfigs.Where(x => x.WorkspaceId == workspaceId).Select(x => x.Settings).ToListAsync(ct);

    public void Add(ExtensionConfig config) => db.ExtensionConfigs.Add(config);

    public async Task<IReadOnlySet<string>> ExistingImageIdsAsync(
        Guid workspaceId, IEnumerable<string> imageIds, CancellationToken ct = default)
    {
        var ids = imageIds.Distinct().ToList();
        if (ids.Count == 0) return new HashSet<string>();
        var found = await db.ExtensionImages
            .Where(x => x.WorkspaceId == workspaceId && ids.Contains(x.ImageId))
            .Select(x => x.ImageId)
            .ToListAsync(ct);
        return found.ToHashSet();
    }

    public Task<ExtensionImage?> GetImageAsync(Guid workspaceId, string imageId, CancellationToken ct = default) =>
        db.ExtensionImages.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.ImageId == imageId, ct);

    public Task<int> DeleteImagesExceptAsync(
        Guid workspaceId, IReadOnlySet<string> keep, DateTimeOffset before, CancellationToken ct = default)
    {
        var ids = keep.ToList();
        return db.ExtensionImages
            .Where(x => x.WorkspaceId == workspaceId && x.CreatedAt < before && !ids.Contains(x.ImageId))
            .ExecuteDeleteAsync(ct);
    }

    public void Add(ExtensionImage image) => db.ExtensionImages.Add(image);

    public Task<DeviceState?> GetStateAsync(Guid deviceId, CancellationToken ct = default) =>
        db.DeviceStates.FirstOrDefaultAsync(x => x.DeviceId == deviceId, ct);

    public void Add(DeviceState state) => db.DeviceStates.Add(state);

    public async Task<IReadOnlyList<DeviceLog>> ListLogsAsync(Guid deviceId, int take, CancellationToken ct = default)
    {
        var newest = await db.DeviceLogs.Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.T).Take(take).ToListAsync(ct);
        newest.Reverse();
        return newest;
    }

    public async Task<long> LastLogTAsync(Guid deviceId, CancellationToken ct = default) =>
        await db.DeviceLogs.Where(x => x.DeviceId == deviceId).MaxAsync(x => (long?)x.T, ct) ?? 0;

    public async Task PruneLogsAsync(Guid deviceId, int keep, CancellationToken ct = default)
    {
        var cut = await db.DeviceLogs.Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.T).Skip(keep).Select(x => (long?)x.T).FirstOrDefaultAsync(ct);
        if (cut is { } t) await db.DeviceLogs.Where(x => x.DeviceId == deviceId && x.T <= t).ExecuteDeleteAsync(ct);
    }

    public Task ClearLogsAsync(Guid deviceId, CancellationToken ct = default) =>
        db.DeviceLogs.Where(x => x.DeviceId == deviceId).ExecuteDeleteAsync(ct);

    public void AddRange(IEnumerable<DeviceLog> logs) => db.DeviceLogs.AddRange(logs);

    public Task<DeviceCommand?> GetCommandAsync(Guid deviceId, Guid commandId, CancellationToken ct = default) =>
        db.DeviceCommands.FirstOrDefaultAsync(x => x.DeviceId == deviceId && x.Id == commandId, ct);

    public async Task<IReadOnlyList<DeviceCommand>> ListOpenCommandsAsync(Guid deviceId, CancellationToken ct = default) =>
        await db.DeviceCommands.Where(x => x.DeviceId == deviceId && (x.Status == CommandStatus.Pending || x.Status == CommandStatus.Sent))
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public void Add(DeviceCommand command) => db.DeviceCommands.Add(command);
}

public sealed class MemberRepository(AppDbContext db) : IMemberRepository
{
    public async Task<IReadOnlyList<WorkspaceMember>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Members.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.InvitedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<WorkspaceMember>> ListByUserAsync(Guid userId, CancellationToken ct = default) =>
        await db.Members.Where(x => x.UserId == userId).ToListAsync(ct);

    public async Task<IReadOnlyList<WorkspaceMember>> ListByWorkspacesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default)
    {
        var ids = workspaceIds.ToList();
        return await db.Members.Where(x => ids.Contains(x.WorkspaceId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkspaceMember>> ListPendingAsync(string normalizedEmail, CancellationToken ct = default) =>
        await db.Members.Where(x => x.Email == normalizedEmail && x.UserId == null).ToListAsync(ct);

    public Task<WorkspaceMember?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Members.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<WorkspaceMember?> GetForUserAsync(Guid workspaceId, Guid userId, CancellationToken ct = default) =>
        db.Members.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.UserId == userId, ct);

    public void Add(WorkspaceMember member) => db.Members.Add(member);

    public void Remove(WorkspaceMember member) => db.Members.Remove(member);
}

public sealed class PlanRepository(AppDbContext db) : IPlanRepository
{
    public async Task<IReadOnlyList<PlanSetting>> ListAsync(CancellationToken ct = default) =>
        await db.Plans.OrderBy(x => x.Price).ToListAsync(ct);

    public async Task<PlanSetting> GetAsync(PlanKey key, CancellationToken ct = default) =>
        await db.Plans.FirstOrDefaultAsync(x => x.Key == key, ct)
        ?? throw new InvalidOperationException($"plan {key} missing from plan_settings");
}

public sealed class TransactionRepository(AppDbContext db) : ITransactionRepository
{
    public async Task<IReadOnlyList<Transaction>> ListByUserAsync(Guid userId, CancellationToken ct = default) =>
        await db.Transactions.Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Transaction>> ListAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await db.Transactions.Where(x => x.CreatedAt >= since).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);

    public Task<Transaction?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Transactions.FirstOrDefaultAsync(x => x.Id == id, ct);

    public Task<Transaction?> GetByInvoiceAsync(string stripeInvoiceId, CancellationToken ct = default) =>
        db.Transactions.FirstOrDefaultAsync(x => x.StripeInvoiceId == stripeInvoiceId, ct);

    public Task<Transaction?> GetChargeByPaymentIntentAsync(string paymentIntentId, CancellationToken ct = default) =>
        db.Transactions.FirstOrDefaultAsync(x => x.StripePaymentIntentId == paymentIntentId && x.Type == TransactionType.Charge, ct);

    public Task<Transaction?> GetByRefundAsync(string stripeRefundId, CancellationToken ct = default) =>
        db.Transactions.FirstOrDefaultAsync(x => x.StripeRefundId == stripeRefundId, ct);

    public void Add(Transaction transaction) => db.Transactions.Add(transaction);
}

public sealed class AuditRepository(AppDbContext db) : IAuditRepository
{
    public async Task<IReadOnlyList<AuditEntry>> ListAsync(Guid? customerId, int take, CancellationToken ct = default) =>
        await db.Audit.Where(x => customerId == null || x.CustomerId == customerId)
            .OrderByDescending(x => x.At).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<AuditEntry>> ListPlanChangesAsync(DateTimeOffset since, CancellationToken ct = default) =>
        await db.Audit.Where(x => x.Action == AuditAction.PlanChanged && x.At >= since).OrderBy(x => x.At).ToListAsync(ct);

    public void Add(AuditEntry entry) => db.Audit.Add(entry);
}

public sealed class PaymentEventRepository(AppDbContext db) : IPaymentEventRepository
{
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => db.PaymentEvents.AnyAsync(x => x.Key == key, ct);

    public void Add(ProcessedPaymentEvent processed) => db.PaymentEvents.Add(processed);
}

public sealed class PromoRepository(AppDbContext db) : IPromoRepository
{
    public async Task<IReadOnlyList<Promo>> ListAsync(CancellationToken ct = default) =>
        await db.Promos.OrderByDescending(x => x.ExpiresAt).ToListAsync(ct);

    public Task<Promo?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        db.Promos.FirstOrDefaultAsync(x => x.Code == code, ct);

    public void Add(Promo promo) => db.Promos.Add(promo);
}

public sealed class DeviceEventRepository(AppDbContext db) : IDeviceEventRepository
{
    public async Task<IReadOnlyList<DeviceEvent>> ListAfterAsync(Guid workspaceId, long afterSeq, int take, CancellationToken ct = default) =>
        await db.DeviceEvents.Where(x => x.WorkspaceId == workspaceId && x.Seq > afterSeq)
            .OrderBy(x => x.Seq).Take(take).ToListAsync(ct);

    public async Task<long> HeadAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.DeviceEvents.Where(x => x.WorkspaceId == workspaceId).MaxAsync(x => (long?)x.Seq, ct) ?? 0;

    public async Task PruneAsync(Guid deviceId, int keep, CancellationToken ct = default)
    {
        var cut = await db.DeviceEvents.Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.Seq).Skip(keep).Select(x => (long?)x.Seq).FirstOrDefaultAsync(ct);
        if (cut is { } s) await db.DeviceEvents.Where(x => x.DeviceId == deviceId && x.Seq <= s).ExecuteDeleteAsync(ct);
    }

    public void Add(DeviceEvent e) => db.DeviceEvents.Add(e);
}

public sealed class CollectionRepository(AppDbContext db) : ICollectionRepository
{
    public async Task<IReadOnlyList<PostCollection>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Collections.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);

    public Task<PostCollection?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Collections.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.Collections.CountAsync(x => x.WorkspaceId == workspaceId, ct);

    public async Task<int> MaxSortOrderAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Collections.Where(x => x.WorkspaceId == workspaceId).MaxAsync(x => (int?)x.SortOrder, ct) ?? -1;

    public void Add(PostCollection collection) => db.Collections.Add(collection);

    public void Remove(PostCollection collection) => db.Collections.Remove(collection);

    public void RemoveRange(IEnumerable<PostCollection> collections) => db.Collections.RemoveRange(collections);
}

public sealed class CollectionPostRepository(AppDbContext db) : ICollectionPostRepository
{
    public async Task<IReadOnlyList<CollectionPost>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.CollectionPosts.AsNoTracking().Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);

    public async Task<IReadOnlyList<CollectionPost>> ListByCollectionAsync(Guid workspaceId, Guid collectionId, CancellationToken ct = default) =>
        await db.CollectionPosts.Where(x => x.WorkspaceId == workspaceId && x.CollectionId == collectionId)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);

    public Task<CollectionPost?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.CollectionPosts.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public async Task<IReadOnlyList<CollectionPost>> ListByIdsAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return await db.CollectionPosts.Where(x => x.WorkspaceId == workspaceId && list.Contains(x.Id)).ToListAsync(ct);
    }

    public Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.CollectionPosts.CountAsync(x => x.WorkspaceId == workspaceId, ct);

    public void Add(CollectionPost post) => db.CollectionPosts.Add(post);

    public void AddRange(IEnumerable<CollectionPost> posts) => db.CollectionPosts.AddRange(posts);

    public void Remove(CollectionPost post) => db.CollectionPosts.Remove(post);

    public void RemoveRange(IEnumerable<CollectionPost> posts) => db.CollectionPosts.RemoveRange(posts);
}

public sealed class LinkSetRepository(AppDbContext db) : ILinkSetRepository
{
    public async Task<IReadOnlyList<LinkSet>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.LinkSets.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);

    public Task<LinkSet?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.LinkSets.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<LinkSet?> GetByNameAsync(Guid workspaceId, string name, CancellationToken ct = default) =>
        db.LinkSets.OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Name == name, ct);

    public Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.LinkSets.CountAsync(x => x.WorkspaceId == workspaceId, ct);

    public async Task<int> MaxSortOrderAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.LinkSets.Where(x => x.WorkspaceId == workspaceId).MaxAsync(x => (int?)x.SortOrder, ct) ?? -1;

    public void Add(LinkSet set) => db.LinkSets.Add(set);

    public void Remove(LinkSet set) => db.LinkSets.Remove(set);

    public void RemoveRange(IEnumerable<LinkSet> sets) => db.LinkSets.RemoveRange(sets);
}

public sealed class SetLinkRepository(AppDbContext db) : ISetLinkRepository
{
    public async Task<IReadOnlyList<SetLink>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.SetLinks.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.LinkSetId).ThenBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<SetLink>> ListBySetAsync(Guid workspaceId, Guid linkSetId, CancellationToken ct = default) =>
        await db.SetLinks.Where(x => x.WorkspaceId == workspaceId && x.LinkSetId == linkSetId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);

    public Task<SetLink?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.SetLinks.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public async Task<IReadOnlyList<SetLink>> ListByIdsAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToList();
        return await db.SetLinks.Where(x => x.WorkspaceId == workspaceId && list.Contains(x.Id)).ToListAsync(ct);
    }

    public Task<int> CountBySetAsync(Guid linkSetId, CancellationToken ct = default) =>
        db.SetLinks.CountAsync(x => x.LinkSetId == linkSetId, ct);

    public async Task<int> MaxSortOrderAsync(Guid linkSetId, CancellationToken ct = default) =>
        await db.SetLinks.Where(x => x.LinkSetId == linkSetId).MaxAsync(x => (int?)x.SortOrder, ct) ?? -1;

    public void Add(SetLink link) => db.SetLinks.Add(link);

    public void AddRange(IEnumerable<SetLink> links) => db.SetLinks.AddRange(links);

    public void Remove(SetLink link) => db.SetLinks.Remove(link);

    public void RemoveRange(IEnumerable<SetLink> links) => db.SetLinks.RemoveRange(links);
}

public sealed class ScheduleRepository(AppDbContext db) : IScheduleRepository
{
    public async Task<IReadOnlyList<Schedule>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Schedules.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Schedule>> ListActiveAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Schedules.Where(x => x.WorkspaceId == workspaceId && x.Active).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public Task<Schedule?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Schedules.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

    public Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.Schedules.CountAsync(x => x.WorkspaceId == workspaceId, ct);

    public async Task<IReadOnlyList<Schedule>> ListByCollectionAsync(Guid workspaceId, Guid collectionId, CancellationToken ct = default) =>
        await db.Schedules.Where(x => x.WorkspaceId == workspaceId && x.CollectionId == collectionId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Schedule>> ListByLinkSetAsync(Guid workspaceId, Guid linkSetId, CancellationToken ct = default) =>
        await db.Schedules.Where(x => x.WorkspaceId == workspaceId && x.LinkSetId == linkSetId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public void Add(Schedule schedule) => db.Schedules.Add(schedule);

    public void Remove(Schedule schedule) => db.Schedules.Remove(schedule);

    public void RemoveRange(IEnumerable<Schedule> schedules) => db.Schedules.RemoveRange(schedules);
}

public sealed class ReportShareRepository(AppDbContext db) : IReportShareRepository
{
    public Task<ReportShare?> GetByTokenAsync(string token, CancellationToken ct = default) =>
        db.ReportShares.AsNoTracking().FirstOrDefaultAsync(x => x.Token == token, ct);

    public Task<int> CountActiveAsync(Guid workspaceId, DateTimeOffset now, CancellationToken ct = default) =>
        db.ReportShares.CountAsync(x => x.WorkspaceId == workspaceId && x.ExpiresAt > now, ct);

    public Task DeleteExpiredAsync(DateTimeOffset before, CancellationToken ct = default) =>
        db.ReportShares.Where(x => x.ExpiresAt < before).ExecuteDeleteAsync(ct);

    public void Add(ReportShare share) => db.ReportShares.Add(share);
}
