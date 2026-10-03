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

public interface ISnippetRepository
{
    Task<IReadOnlyList<Snippet>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(Snippet snippet);
}
