namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Forgets every change that was not saved and every loaded entity. For recovering from a failed save (a unique
    /// index said no): entities loaded before are detached, so the caller loads what it still needs again.
    /// </summary>
    void DiscardChanges();
}
