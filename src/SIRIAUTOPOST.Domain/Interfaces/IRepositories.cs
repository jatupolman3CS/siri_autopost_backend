using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    void Add(User user);
}

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListAllAsync(CancellationToken ct = default);
    /// <summary>The user's role as a member (not as owner); null when they are not one.</summary>
    Task<WorkspaceRole?> GetMemberRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    void Add(Workspace workspace);
}

public interface IMemberRepository
{
    Task<IReadOnlyList<WorkspaceMember>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListByWorkspacesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListPendingAsync(string normalizedEmail, CancellationToken ct = default);
    Task<WorkspaceMember?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<WorkspaceMember?> GetForUserAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    void Add(WorkspaceMember member);
    void Remove(WorkspaceMember member);
}

public interface IPlanRepository
{
    Task<IReadOnlyList<PlanSetting>> ListAsync(CancellationToken ct = default);
    Task<PlanSetting> GetAsync(PlanKey key, CancellationToken ct = default);
}

public interface ITransactionRepository
{
    Task<IReadOnlyList<Transaction>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Transaction>> ListAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<Transaction?> GetAsync(Guid id, CancellationToken ct = default);
    void Add(Transaction transaction);
}

public interface IAuditRepository
{
    /// <summary>Newest first; customerId null = every entry.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(Guid? customerId, int take, CancellationToken ct = default);
    /// <summary>Every plan change since a time (the plan history behind churn and MRR).</summary>
    Task<IReadOnlyList<AuditEntry>> ListPlanChangesAsync(DateTimeOffset since, CancellationToken ct = default);
    void Add(AuditEntry entry);
}

public interface IPromoRepository
{
    Task<IReadOnlyList<Promo>> ListAsync(CancellationToken ct = default);
    Task<Promo?> GetByCodeAsync(string code, CancellationToken ct = default);
    void Add(Promo promo);
}

public interface IAccountRepository
{
    Task<IReadOnlyList<SocialAccount>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Accounts connected through a paired browser, in any of these workspaces.</summary>
    Task<int> CountConnectedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    Task<IReadOnlyList<SocialAccount>> ListConnectedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    Task<SocialAccount?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<SocialAccount?> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(SocialAccount account);
}

public interface IPostRepository
{
    Task<Post?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListOpenErrorsAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListByStatusAsync(Guid workspaceId, PostStatus status, CancellationToken ct = default);
    /// <summary>Posts sent in the last 7 days, per workspace.</summary>
    Task<Dictionary<Guid, int>> CountSentSinceAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>Queued posts of an account that are due at <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<Post>> ListDueAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Posts a device has taken and not reported on yet.</summary>
    Task<IReadOnlyList<Post>> ListClaimedByAsync(Guid deviceId, CancellationToken ct = default);
    /// <summary>Published posts of a platform in a workspace (connected accounts only, see below).</summary>
    Task<int> CountPublishedSinceAsync(Guid workspaceId, Platform platform, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>When the account last published something (the anti-ban gap is per account).</summary>
    Task<DateTimeOffset?> LastPublishedAtAsync(Guid accountId, CancellationToken ct = default);
    // The queries below count only posts of accounts connected through a paired browser: the sample
    // accounts' history of a new workspace never went out, so it counts toward no limit or report.

    /// <summary>Posts published in these workspaces since a time (the plan's posts per 24 hours).</summary>
    Task<int> CountPublishedSinceAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>Counts per status of posts scheduled in [from, to), per workspace.</summary>
    Task<IReadOnlyList<(Guid WorkspaceId, PostStatus Status, int Count)>> CountByStatusAsync(
        IEnumerable<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    /// <summary>The newest posts of these workspaces scheduled before a time, any status.</summary>
    Task<IReadOnlyList<Post>> ListRecentAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset before, int take, CancellationToken ct = default);
    /// <summary>Failed posts not dismissed yet.</summary>
    Task<IReadOnlyList<Post>> ListFailedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    void Add(Post post);
    void Remove(Post post);
}

/// <summary>A library entry without its bytes.</summary>
public sealed record MediaSummary(Guid Id, string Name, string ContentType, MediaKind Kind, long Size, int UsedCount, DateTimeOffset CreatedAt);

public interface IMediaRepository
{
    Task<IReadOnlyList<MediaSummary>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<MediaFile?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<int> CountExistingAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default);
    void Add(MediaFile file);
}

public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<Device?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Device?> GetByKeyHashAsync(string keyHash, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListByWorkspacesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    void Add(Device device);
    void Remove(Device device);
}

public interface IDevicePairingRepository
{
    Task<DevicePairing?> GetByCodeAsync(string code, CancellationToken ct = default);
    void Add(DevicePairing pairing);
}

public interface ISnippetRepository
{
    Task<IReadOnlyList<Snippet>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(Snippet snippet);
}
