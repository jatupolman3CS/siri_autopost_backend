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
    public DbSet<Snippet> Snippets => Set<Snippet>();
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

    /// <summary>
    /// Saves, then hands the device events written in this save (now with their Seq) to the streams waiting for
    /// them. Publishing after the commit means a stream never announces something that was rolled back, and a
    /// client that connects in between still finds the events by Seq.
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // A unique index said no (a retried webhook, two saves racing): callers that expect it catch this.
            throw new DuplicateKeyException(pg.ConstraintName ?? "");
        }
        if (added is { Count: > 0 }) events!.Publish(added);
        return n;
    }
}
