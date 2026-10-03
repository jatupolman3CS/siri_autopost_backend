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

    public void Add(User user) => db.Users.Add(user);
}

public sealed class WorkspaceRepository(AppDbContext db) : IWorkspaceRepository
{
    public Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Workspaces.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<Workspace>> ListByOwnerAsync(Guid ownerId, CancellationToken ct = default) =>
        await db.Workspaces.Where(x => x.OwnerId == ownerId).OrderBy(x => x.CreatedAt).ToListAsync(ct);

    public void Add(Workspace workspace) => db.Workspaces.Add(workspace);
}

public sealed class AccountRepository(AppDbContext db) : IAccountRepository
{
    public async Task<IReadOnlyList<SocialAccount>> ListAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.Accounts.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);

    public Task<SocialAccount?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default) =>
        db.Accounts.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == id, ct);

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
