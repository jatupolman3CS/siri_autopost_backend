using Microsoft.EntityFrameworkCore;

namespace SIRI.AUTOPOST.Server.Data;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// One config (what the extension calls "settings": campaigns, groups, posts,
// global settings, Telegram). Several devices may use the same profile.
public class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    // Raw settings JSON of the extension (version 2 shape); null = empty.
    public string? Settings { get; set; }
    // Bumped on every settings save; devices pull when it changes.
    public long Revision { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Image / video of a profile, same id as the extension's "img:<id>" key.
public class ProfileImage
{
    public Guid ProfileId { get; set; }
    public string ImageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A computer running the extension. It signs in with its device key.
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public Guid? ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string Version { get; set; } = "";
    // Latest extension state (running, campaigns, queue...) as JSON.
    public string? State { get; set; }
    public DateTime? StateAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeviceLog
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public long T { get; set; } // ms since epoch, from the extension
    public string Level { get; set; } = "";
    public string Msg { get; set; } = "";
}

// Remote command for a device (start, stop, test post...).
public class DeviceCommand
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public string Cmd { get; set; } = "";
    public string Args { get; set; } = "{}";
    public string Status { get; set; } = CommandStatus.Pending;
    public string? Result { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? DoneAt { get; set; }
}

public static class CommandStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Done = "done";
    public const string Expired = "expired";
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    // Tables share the database with other apps: every name starts with fbap_.
    public const string MigrationsTable = "FBAP_EF_MIGRATIONS";

    public DbSet<User> Users => Set<User>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileImage> ProfileImages => Set<ProfileImage>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceLog> DeviceLogs => Set<DeviceLog>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("FBAP_USERS");
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(100);
        });
        b.Entity<Profile>(e =>
        {
            e.ToTable("FBAP_PROFILES");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Settings).HasColumnType("jsonb");
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
        });
        b.Entity<ProfileImage>(e =>
        {
            e.ToTable("FBAP_PROFILE_IMAGES");
            e.HasKey(x => new { x.ProfileId, x.ImageId });
            e.Property(x => x.ImageId).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.HasOne<Profile>().WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Device>(e =>
        {
            e.ToTable("FBAP_DEVICES");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.State).HasColumnType("jsonb");
            e.Property(x => x.Version).HasMaxLength(50);
            e.HasOne(x => x.Profile).WithMany().HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<DeviceLog>(e =>
        {
            e.ToTable("FBAP_DEVICE_LOGS");
            e.HasIndex(x => new { x.DeviceId, x.Id });
            e.Property(x => x.Level).HasMaxLength(20);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DeviceCommand>(e =>
        {
            e.ToTable("FBAP_DEVICE_COMMANDS");
            e.HasIndex(x => new { x.DeviceId, x.Status });
            e.Property(x => x.Cmd).HasMaxLength(50);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Args).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
