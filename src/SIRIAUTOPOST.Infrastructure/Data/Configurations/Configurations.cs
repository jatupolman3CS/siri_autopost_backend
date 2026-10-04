using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Infrastructure.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("USERS");
        b.Property(x => x.Email).HasMaxLength(254).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.PasswordHash).HasMaxLength(300);
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.StripeCustomerId).HasMaxLength(100);
        b.Property(x => x.StripeSubscriptionId).HasMaxLength(100);
        // Webhooks find the customer by the Stripe id; NULLs (every user without one) do not clash in a unique index.
        b.HasIndex(x => x.StripeCustomerId).IsUnique();
        b.OwnsOne(x => x.Limits, o => o.ToJson("limit_overrides"));
    }
}

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable("WORKSPACES");
        b.Property(x => x.Name).HasMaxLength(Workspace.MaxNameLength).IsRequired();
        b.HasIndex(x => x.OwnerId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        // Settings are read and written as a whole, so each is one jsonb column.
        b.OwnsOne(x => x.AntiBan, o =>
        {
            o.ToJson("anti_ban");
            o.OwnsOne(x => x.Limits);
            o.OwnsOne(x => x.Advanced);
        });
        b.OwnsOne(x => x.Offline, o => o.ToJson("offline"));
        // Notification rules hold dictionaries (by set and link id), which owned JSON cannot map: a whole document.
        b.Property(x => x.Notifications).HasColumnName("notifications").HasColumnType("jsonb")
            .HasJsonConversion(() => new NotificationSettings());
        b.Property(x => x.AutoReply).HasColumnName("auto_reply").HasColumnType("jsonb")
            .HasJsonConversion(() => new AutoReplySettings());
    }
}

/// <summary>Maps a class to one jsonb column through System.Text.Json (camelCase, snake_case enums), compared by content.</summary>
internal static class JsonColumn
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json, Func<T> empty) where T : class =>
        string.IsNullOrWhiteSpace(json) ? empty() : JsonSerializer.Deserialize<T>(json, Options) ?? empty();

    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property, Func<T> empty) where T : class
    {
        property.HasConversion(
            v => Write(v),
            s => Read(s, empty),
            new ValueComparer<T>(
                (a, b) => Write(a) == Write(b),
                v => Write(v).GetHashCode(),
                v => Read(Write(v), empty)));
        return property;
    }
}

public sealed class SocialAccountConfiguration : IEntityTypeConfiguration<SocialAccount>
{
    public void Configure(EntityTypeBuilder<SocialAccount> b)
    {
        b.ToTable("SOCIAL_ACCOUNTS");
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
        b.ToTable("POSTS");
        // Optimistic concurrency on PostgreSQL's own row version (the system column xmin; no DDL): two requests that read
        // the same post cannot both change it, the second save fails with a ConcurrencyConflictException.
        b.Property<uint>("xmin").IsRowVersion();
        b.Property(x => x.Target).HasMaxLength(Post.MaxTargetLength);
        b.Property(x => x.Content).HasMaxLength(Post.MaxContentLength).IsRequired();
        b.Property(x => x.FailureDetail).HasMaxLength(Post.MaxDetailLength);
        b.Property(x => x.TargetKey).HasMaxLength(60);
        b.Property(x => x.SlotKey).HasMaxLength(20);
        b.Property(x => x.TargetUrl).HasMaxLength(Post.MaxTargetUrlLength);
        b.Property(x => x.Code).HasMaxLength(Post.MaxCodeLength);
        // A schedule's run is idempotent: one post per target and slot. Posts without a schedule are not constrained.
        b.HasIndex(x => new { x.ScheduleId, x.TargetKey, x.SlotKey }).IsUnique().HasFilter("schedule_id IS NOT NULL");
        b.HasIndex(x => new { x.ScheduleId, x.Status, x.ScheduledAt }).HasFilter("schedule_id IS NOT NULL");
        b.HasIndex(x => new { x.LinkId, x.ScheduledAt }).HasFilter("link_id IS NOT NULL");
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
        b.ToTable("MEDIA_FILES");
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.ExternalUrl).HasMaxLength(2000);
        b.HasIndex(x => new { x.WorkspaceId, x.FolderId });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MediaFolderConfiguration : IEntityTypeConfiguration<MediaFolder>
{
    public void Configure(EntityTypeBuilder<MediaFolder> b)
    {
        b.ToTable("MEDIA_FOLDERS");
        b.Property(x => x.Name).HasMaxLength(MediaFolder.MaxNameLength).IsRequired();
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImportedPostConfiguration : IEntityTypeConfiguration<ImportedPost>
{
    public void Configure(EntityTypeBuilder<ImportedPost> b)
    {
        b.ToTable("IMPORTED_POSTS");
        b.Property(x => x.Source).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.Title).HasMaxLength(500);
        b.Property(x => x.Text).HasColumnType("text").IsRequired();
        b.Property(x => x.LinkUrl).HasMaxLength(2000);
        b.OwnsMany(x => x.Media, o => o.ToJson("media"));
        b.HasIndex(x => new { x.WorkspaceId, x.Source, x.SourceKey }).IsUnique();
        b.HasIndex(x => new { x.WorkspaceId, x.PostedAt });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SnippetConfiguration : IEntityTypeConfiguration<Snippet>
{
    public void Configure(EntityTypeBuilder<Snippet> b)
    {
        b.ToTable("SNIPPETS");
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
        b.ToTable("DEVICES");
        b.Property(x => x.Name).HasMaxLength(Device.MaxNameLength).IsRequired();
        b.Property(x => x.Browser).HasMaxLength(120);
        b.Property(x => x.Version).HasMaxLength(40);
        b.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.AutoPauseReason).HasMaxLength(200);
        b.HasIndex(x => x.KeyHash).IsUnique();
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DevicePairingConfiguration : IEntityTypeConfiguration<DevicePairing>
{
    public void Configure(EntityTypeBuilder<DevicePairing> b)
    {
        b.ToTable("DEVICE_PAIRINGS");
        b.Property(x => x.Code).HasMaxLength(9).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> b)
    {
        b.ToTable("WORKSPACE_MEMBERS");
        b.Property(x => x.Email).HasMaxLength(254).IsRequired();
        b.HasIndex(x => new { x.WorkspaceId, x.Email }).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PlanSettingConfiguration : IEntityTypeConfiguration<PlanSetting>
{
    public void Configure(EntityTypeBuilder<PlanSetting> b)
    {
        b.ToTable("PLAN_SETTINGS");
        b.HasKey(x => x.Key);
        // The design's prices and limits; the platform admin edits them from there.
        b.HasData(PlanSetting.Defaults);
    }
}

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> b)
    {
        b.ToTable("TRANSACTIONS");
        b.Property(x => x.PromoCode).HasMaxLength(30);
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.Property(x => x.StripeInvoiceId).HasMaxLength(100);
        b.Property(x => x.StripePaymentIntentId).HasMaxLength(100);
        b.Property(x => x.StripeRefundId).HasMaxLength(100);
        b.Property(x => x.ReceiptUrl).HasMaxLength(500);
        // One row per Stripe invoice and per Stripe refund: a redelivered webhook cannot record them twice.
        b.HasIndex(x => x.StripeInvoiceId).IsUnique();
        b.HasIndex(x => x.StripeRefundId).IsUnique();
        b.HasIndex(x => x.StripePaymentIntentId);
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProcessedPaymentEventConfiguration : IEntityTypeConfiguration<ProcessedPaymentEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedPaymentEvent> b)
    {
        b.ToTable("PAYMENT_EVENTS");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(150);
        b.Property(x => x.Type).HasMaxLength(60).IsRequired();
        b.HasIndex(x => x.At);
    }
}

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("AUDIT_ENTRIES");
        b.Property(x => x.From).HasMaxLength(200);
        b.Property(x => x.To).HasMaxLength(200);
        b.HasIndex(x => new { x.CustomerId, x.At });
        b.HasIndex(x => new { x.Action, x.At });
        // No foreign keys: the log outlives deleted users and promo codes.
    }
}

public sealed class PromoConfiguration : IEntityTypeConfiguration<Promo>
{
    public void Configure(EntityTypeBuilder<Promo> b)
    {
        b.ToTable("PROMOS");
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Discount).HasMaxLength(10).IsRequired();
    }
}

public sealed class ExtensionConfigConfiguration : IEntityTypeConfiguration<ExtensionConfig>
{
    public void Configure(EntityTypeBuilder<ExtensionConfig> b)
    {
        b.ToTable("EXTENSION_CONFIGS");
        // text, not jsonb: the JSON is compared byte for byte to keep the revision on identical saves.
        b.Property(x => x.Settings).HasColumnType("text").IsRequired();
        b.HasIndex(x => x.DeviceId).IsUnique();
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExtensionImageConfiguration : IEntityTypeConfiguration<ExtensionImage>
{
    public void Configure(EntityTypeBuilder<ExtensionImage> b)
    {
        b.ToTable("EXTENSION_IMAGES");
        b.Property(x => x.ImageId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.HasIndex(x => new { x.WorkspaceId, x.ImageId }).IsUnique();
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceStateConfiguration : IEntityTypeConfiguration<DeviceState>
{
    public void Configure(EntityTypeBuilder<DeviceState> b)
    {
        b.ToTable("DEVICE_STATES");
        b.Property(x => x.Json).HasColumnType("text").IsRequired();
        b.HasIndex(x => x.DeviceId).IsUnique();
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceLogConfiguration : IEntityTypeConfiguration<DeviceLog>
{
    public void Configure(EntityTypeBuilder<DeviceLog> b)
    {
        b.ToTable("DEVICE_LOGS");
        b.Property(x => x.Level).HasMaxLength(20);
        b.Property(x => x.Message).HasMaxLength(DeviceLog.MaxMessageLength);
        b.HasIndex(x => new { x.DeviceId, x.T });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceCommandConfiguration : IEntityTypeConfiguration<DeviceCommand>
{
    public void Configure(EntityTypeBuilder<DeviceCommand> b)
    {
        b.ToTable("DEVICE_COMMANDS");
        b.Property(x => x.Cmd).HasMaxLength(40).IsRequired();
        b.Property(x => x.Args).HasColumnType("text").IsRequired();
        b.Property(x => x.Result).HasColumnType("text");
        b.HasIndex(x => new { x.DeviceId, x.Status });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeviceEventConfiguration : IEntityTypeConfiguration<DeviceEvent>
{
    public void Configure(EntityTypeBuilder<DeviceEvent> b)
    {
        b.ToTable("DEVICE_EVENTS");
        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).UseIdentityAlwaysColumn();
        b.Property(x => x.Type).HasMaxLength(40).IsRequired();
        b.Property(x => x.Payload).HasColumnType("text").IsRequired();
        b.HasIndex(x => new { x.WorkspaceId, x.Seq });
        b.HasIndex(x => new { x.DeviceId, x.Seq });
        // No foreign key: a "device.revoked" event outlives its device.
    }
}

public sealed class PostCollectionConfiguration : IEntityTypeConfiguration<PostCollection>
{
    public void Configure(EntityTypeBuilder<PostCollection> b)
    {
        b.ToTable("POST_COLLECTIONS");
        b.Property(x => x.Name).HasMaxLength(PostCollection.MaxNameLength).IsRequired();
        b.Property(x => x.Description).HasMaxLength(PostCollection.MaxDescriptionLength);
        b.Property(x => x.Icon).HasMaxLength(PostCollection.MaxIconLength);
        b.HasIndex(x => new { x.WorkspaceId, x.SortOrder });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.OwnsOne(x => x.Settings, o => o.ToJson("settings"));
    }
}

public sealed class CollectionPostConfiguration : IEntityTypeConfiguration<CollectionPost>
{
    public void Configure(EntityTypeBuilder<CollectionPost> b)
    {
        b.ToTable("COLLECTION_POSTS");
        b.Property(x => x.Text).HasMaxLength(CollectionPost.MaxTextLength).IsRequired();
        b.HasIndex(x => new { x.CollectionId, x.CreatedAt });
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<PostCollection>().WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class LinkSetConfiguration : IEntityTypeConfiguration<LinkSet>
{
    public void Configure(EntityTypeBuilder<LinkSet> b)
    {
        b.ToTable("LINK_SETS");
        b.Property(x => x.Name).HasMaxLength(LinkSet.MaxNameLength).IsRequired();
        b.HasIndex(x => new { x.WorkspaceId, x.SortOrder });
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        // PostAsAccountId and AccountIds point at accounts without foreign keys: an unbound account stays, but nothing breaks if one goes.
    }
}

public sealed class SetLinkConfiguration : IEntityTypeConfiguration<SetLink>
{
    public void Configure(EntityTypeBuilder<SetLink> b)
    {
        b.ToTable("SET_LINKS");
        b.Property(x => x.Name).HasMaxLength(SetLink.MaxNameLength);
        b.Property(x => x.Url).HasMaxLength(SetLink.MaxUrlLength);
        b.Property(x => x.Code).HasMaxLength(SetLink.MaxCodeLength);
        b.HasIndex(x => new { x.LinkSetId, x.SortOrder });
        b.HasIndex(x => x.WorkspaceId);
        b.HasOne<LinkSet>().WithMany().HasForeignKey(x => x.LinkSetId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ScheduleConfiguration : IEntityTypeConfiguration<Schedule>
{
    public void Configure(EntityTypeBuilder<Schedule> b)
    {
        b.ToTable("SCHEDULES");
        b.Property(x => x.Name).HasMaxLength(Schedule.MaxNameLength).IsRequired();
        b.Property(x => x.FirstTime).HasMaxLength(5);
        b.Property(x => x.OnceTime).HasMaxLength(5);
        b.Property(x => x.DripFrom).HasMaxLength(5);
        b.Property(x => x.DripTo).HasMaxLength(5);
        b.Property(x => x.Overrides).HasColumnType("jsonb")
            .HasJsonConversion(() => new Dictionary<string, List<string>>());
        b.HasIndex(x => new { x.WorkspaceId, x.Active });
        b.HasIndex(x => x.CollectionId);
        b.HasIndex(x => x.LinkSetId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        // A collection or link set in use is refused by the handlers (422); the database says no as well. NO ACTION is
        // checked at the end of the statement, so deleting a whole workspace (which cascades to both) still works.
        b.HasOne<PostCollection>().WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<LinkSet>().WithMany().HasForeignKey(x => x.LinkSetId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class ReportShareConfiguration : IEntityTypeConfiguration<ReportShare>
{
    public void Configure(EntityTypeBuilder<ReportShare> b)
    {
        b.ToTable("REPORT_SHARES");
        b.Property(x => x.Token).HasMaxLength(ReportShare.TokenLength).IsRequired();
        b.Property(x => x.Brand).HasMaxLength(ReportShare.MaxBrandLength).IsRequired();
        b.Property(x => x.Period).HasMaxLength(10).IsRequired();
        b.Property(x => x.SnapshotJson).HasColumnType("text").IsRequired();
        b.HasIndex(x => x.Token).IsUnique();
        b.HasIndex(x => x.WorkspaceId);
        b.HasIndex(x => x.ExpiresAt);
        b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
