using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IPostRepository
{
    Task<Post?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListAsync(CancellationToken ct = default);
    void Add(Post post);
    void Remove(Post post);
}
