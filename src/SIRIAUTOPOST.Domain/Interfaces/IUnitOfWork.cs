namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Forgets every change that was not saved and every loaded entity. For recovering from a failed save (a unique
    /// index said no): entities loaded before are detached, so the caller loads what it still needs again.
    /// </summary>
    void DiscardChanges();

    /// <summary>
    /// Runs <paramref name="work"/> as one database transaction: every save inside it is committed together when it
    /// returns and rolled back when it throws. With a <paramref name="lockKey"/> the transaction first takes an
    /// exclusive lock on that name, so runs with the same key go one after another (even across API instances) instead
    /// of racing. A call made inside a running transaction joins it (and takes its lock, kept until the outer one ends).
    /// What was read before the lock may be out of date: call <see cref="DiscardChanges"/> and read again inside.
    /// A lock that is not granted within a while, or a deadlock, surfaces as a <c>ConcurrencyConflictException</c>.
    /// </summary>
    Task ExecuteInTransactionAsync(string? lockKey, Func<Task> work, CancellationToken ct = default);

    /// <summary>True while <see cref="ExecuteInTransactionAsync"/> is running: a failed save then leaves the whole transaction unusable.</summary>
    bool InTransaction { get; }
}
