using Microsoft.EntityFrameworkCore;
using Npgsql;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, IDeviceEventBus? events = null) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<SocialAccount> Accounts => Set<SocialAccount>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<MediaFile> Media => Set<MediaFile>();
    public DbSet<MediaFolder> MediaFolders => Set<MediaFolder>();
    public DbSet<Snippet> Snippets => Set<Snippet>();
    public DbSet<ImportedPost> ImportedPosts => Set<ImportedPost>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();
    public DbSet<WorkspaceMember> Members => Set<WorkspaceMember>();
    public DbSet<PlanSetting> Plans => Set<PlanSetting>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Promo> Promos => Set<Promo>();
    public DbSet<ProcessedPaymentEvent> PaymentEvents => Set<ProcessedPaymentEvent>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<ExtensionConfig> ExtensionConfigs => Set<ExtensionConfig>();
    public DbSet<ExtensionImage> ExtensionImages => Set<ExtensionImage>();
    public DbSet<DeviceState> DeviceStates => Set<DeviceState>();
    public DbSet<DeviceLog> DeviceLogs => Set<DeviceLog>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();
    public DbSet<PostCollection> Collections => Set<PostCollection>();
    public DbSet<CollectionPost> CollectionPosts => Set<CollectionPost>();
    public DbSet<CollectionMember> CollectionMembers => Set<CollectionMember>();
    public DbSet<LinkSet> LinkSets => Set<LinkSet>();
    public DbSet<SetLink> SetLinks => Set<SetLink>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ReportShare> ReportShares => Set<ReportShare>();

    // Picks up every IEntityTypeConfiguration in Data/Configurations.
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    // Enums are stored by name, so the database stays readable and reordering members is safe.
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);
    }

    public void DiscardChanges() => ChangeTracker.Clear();

    // Device events saved inside ExecuteInTransactionAsync wait here until the outermost transaction commits.
    private List<DeviceEvent>? deferred;
    private bool inTransaction;

    public bool InTransaction => inTransaction;

    public async Task ExecuteInTransactionAsync(string? lockKey, Func<Task> work, CancellationToken ct = default)
    {
        if (inTransaction)
        {
            // Joins the running transaction: its lock (if any) is kept until the outer transaction ends.
            if (lockKey is not null) await TakeLockAsync(lockKey, ct);
            await work();
            return;
        }
        inTransaction = true;
        deferred = [];
        var committed = false;
        try
        {
            await using var tx = await Database.BeginTransactionAsync(ct);
            try
            {
                // A lock (the advisory one below, or a row another transaction holds) that is not granted in 20 s gives up with a conflict.
                await Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '20s'", ct);
                if (lockKey is not null) await TakeLockAsync(lockKey, ct);
                await work();
                await tx.CommitAsync(ct);
                committed = true;
            }
            catch (PostgresException ex) when (IsRetryable(ex))
            {
                throw new ConcurrencyConflictException();
            }
        }
        finally
        {
            inTransaction = false;
            var pending = deferred;
            deferred = null;
            // Streams hear about the events only once they are committed: a rolled back run announces nothing.
            if (committed && pending is { Count: > 0 }) events?.Publish(pending);
        }
    }

    // pg_advisory_xact_lock: held until the transaction ends, so a crash or an exception never leaves a lock behind.
    private async Task TakeLockAsync(string key, CancellationToken ct)
    {
        try
        {
            await Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        }
        catch (PostgresException ex) when (IsRetryable(ex))
        {
            throw new ConcurrencyConflictException();
        }
    }

    /// <summary>Deadlock, serialization failure, or a lock that was not granted in time: the work can simply be run again.</summary>
    private static bool IsRetryable(PostgresException ex) =>
        ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable;

    /// <summary>
    /// Saves, then hands the device events written in this save (now with their Seq) to the streams waiting for
    /// them. Publishing after the commit means a stream never announces something that was rolled back, and a
    /// client that connects in between still finds the events by Seq. Inside <see cref="ExecuteInTransactionAsync"/>
    /// the events wait for the outermost commit.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var added = events is null
            ? null
            : ChangeTracker.Entries<DeviceEvent>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        int n;
        try
        {
            n = await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A row changed (or went away) after it was read: the save was refused, nothing of it was written.
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // A unique index said no (a retried webhook, two saves racing): callers that expect it catch this.
            throw new DuplicateKeyException(pg.ConstraintName ?? "");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && IsRetryable(pg))
        {
            // The database picked this save as the loser of a race (deadlock, serialization failure, lock timeout).
            throw new ConcurrencyConflictException();
        }
        if (added is { Count: > 0 })
        {
            if (deferred is not null) deferred.AddRange(added);
            else events!.Publish(added);
        }
        return n;
    }
}
