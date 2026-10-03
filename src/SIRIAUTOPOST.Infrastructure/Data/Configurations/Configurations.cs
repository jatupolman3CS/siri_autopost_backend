using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Infrastructure.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.Property(x => x.Email).HasMaxLength(254).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.PasswordHash).HasMaxLength(300);
    }
}

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable("workspaces");
        b.Property(x => x.Name).HasMaxLength(Workspace.MaxNameLength).IsRequired();
        b.HasIndex(x => x.OwnerId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        // Settings are read and written as a whole, so each is one jsonb column.
        b.OwnsOne(x => x.AntiBan, o =>
        {
            o.ToJson("anti_ban");
            o.OwnsOne(x => x.Limits);
        });
        b.OwnsOne(x => x.Offline, o => o.ToJson("offline"));
    }
}

public sealed class SocialAccountConfiguration : IEntityTypeConfiguration<SocialAccount>
{
    public void Configure(EntityTypeBuilder<SocialAccount> b)
    {
        b.ToTable("social_accounts");
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Handle).HasMaxLength(200);
        b.Property(x => x.DefaultTarget).HasMaxLength(120);
        b.HasIndex(x => x.WorkspaceId);
        b.HasIndex(x => x.DeviceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        b.OwnsMany(x => x.GroupLinks, o => o.ToJson("group_links"));
    }
}

public sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> b)
    {
        b.ToTable("posts");
        b.Property(x => x.Target).HasMaxLength(Post.MaxTargetLength);
        b.Property(x => x.Content).HasMaxLength(Post.MaxContentLength).IsRequired();
        b.Property(x => x.FailureDetail).HasMaxLength(Post.MaxDetailLength);
        b.HasIndex(x => new { x.WorkspaceId, x.ScheduledAt });
        b.HasIndex(x => new { x.AccountId, x.Status, x.ScheduledAt });
        b.HasIndex(x => x.ClaimedByDeviceId);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.ClaimedByDeviceId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.WorkspaceId, x.Status });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<SocialAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
{
    public void Configure(EntityTypeBuilder<MediaFile> b)
    {
        b.ToTable("media_files");
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SnippetConfiguration : IEntityTypeConfiguration<Snippet>
{
    public void Configure(EntityTypeBuilder<Snippet> b)
    {
        b.ToTable("snippets");
        b.Property(x => x.Title).HasMaxLength(Snippet.MaxTitleLength).IsRequired();
        b.Property(x => x.Text).HasMaxLength(Snippet.MaxTextLength).IsRequired();
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices");
        b.Property(x => x.Name).HasMaxLength(Device.MaxNameLength).IsRequired();
        b.Property(x => x.Browser).HasMaxLength(120);
        b.Property(x => x.Version).HasMaxLength(40);
        b.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.KeyHash).IsUnique();
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DevicePairingConfiguration : IEntityTypeConfiguration<DevicePairing>
{
    public void Configure(EntityTypeBuilder<DevicePairing> b)
    {
        b.ToTable("device_pairings");
        b.Property(x => x.Code).HasMaxLength(9).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
