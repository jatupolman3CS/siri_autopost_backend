using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default);
    void Add(User user);
}

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    void Add(Workspace workspace);
}

public interface IAccountRepository
{
    Task<IReadOnlyList<SocialAccount>> ListAsync(Guid workspaceId, CancellationToken ct = default);
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
    Task<int> CountPublishedSinceAsync(Guid workspaceId, Platform platform, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>When the account last published something (the anti-ban gap is per account).</summary>
    Task<DateTimeOffset?> LastPublishedAtAsync(Guid accountId, CancellationToken ct = default);
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
