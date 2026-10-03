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

    public void Add(DevicePairing pairing) => db.DevicePairings.Add(pairing);
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

    public void Add(Transaction transaction) => db.Transactions.Add(transaction);
}

public sealed class PromoRepository(AppDbContext db) : IPromoRepository
{
    public async Task<IReadOnlyList<Promo>> ListAsync(CancellationToken ct = default) =>
        await db.Promos.OrderByDescending(x => x.ExpiresAt).ToListAsync(ct);

    public Task<Promo?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        db.Promos.FirstOrDefaultAsync(x => x.Code == code, ct);

    public void Add(Promo promo) => db.Promos.Add(promo);
}
