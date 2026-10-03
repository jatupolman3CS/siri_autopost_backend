using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
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

    // Picks up every IEntityTypeConfiguration in Data/Configurations.
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    // Enums are stored by name, so the database stays readable and reordering members is safe.
    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);
    }
}
