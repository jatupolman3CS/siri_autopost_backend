using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Repositories;

public sealed class PostRepository(AppDbContext db) : IPostRepository
{
    public Task<Post?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Post>> ListAsync(CancellationToken ct = default) =>
        await db.Posts.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(ct);

    public void Add(Post post) => db.Posts.Add(post);

    public void Remove(Post post) => db.Posts.Remove(post);
}
