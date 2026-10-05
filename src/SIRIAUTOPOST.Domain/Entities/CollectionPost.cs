using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Entities;

/// <summary>
/// A master post's own settings, kept as one jsonb document on the post: how it is written and when it may go out. The
/// schedule engine checks them on top of the schedule's own times: a post that does not allow a slot is not drawn for it.
/// </summary>
public class CollectionPostSettings
{
    public const int MaxPerDayLimit = 50;

    /// <summary>Null = follow the collection; "" = none for this post.</summary>
    public string? Hashtags { get; set; }
    /// <summary>Null = follow the collection; "" = none for this post.</summary>
    public string? Footer { get; set; }
    /// <summary>Null = follow the collection.</summary>
    public FooterPosition? FooterPos { get; set; }
    /// <summary>First local day the post may go out (null = no start).</summary>
    public DateOnly? ValidFrom { get; set; }
    /// <summary>Last local day the post may go out (null = no end).</summary>
    public DateOnly? ValidUntil { get; set; }
    /// <summary>Days of the week it may go out (0 = Sunday ... 6 = Saturday, like <see cref="DayOfWeek"/>); empty = every day.</summary>
    public List<int> Weekdays { get; set; } = [];
    /// <summary>"HH:mm" window of the day (both or none); a window whose end is earlier than its start runs past midnight.</summary>
    public string? TimeFrom { get; set; }
    public string? TimeTo { get; set; }
    /// <summary>Most uses of the post per local day over every group (0 = no limit).</summary>
    public int MaxPerDay { get; set; }

    public void Validate()
    {
        Hashtags = Hashtags?.Trim();
        Footer = Footer?.Trim();
        if (Hashtags is { Length: > CollectionSettings.MaxHashtagsLength })
            throw new DomainException($"แฮชแท็กยาวเกิน {CollectionSettings.MaxHashtagsLength} ตัวอักษร");
        if (Footer is { Length: > CollectionSettings.MaxFooterLength })
            throw new DomainException($"ข้อความส่วนท้ายยาวเกิน {CollectionSettings.MaxFooterLength} ตัวอักษร");
        if (FooterPos is { } pos && !Enum.IsDefined(pos)) throw new DomainException("ค่าที่ส่งมาไม่ถูกต้อง");
        if (ValidFrom is { } from && ValidUntil is { } until && from > until)
            throw new DomainException("วันที่เริ่มของโพสต์ต้องไม่เกินวันที่สิ้นสุด");
        Weekdays = (Weekdays ?? []).Distinct().Order().ToList();
        if (Weekdays.Any(d => d is < 0 or > 6)) throw new DomainException("วันในสัปดาห์ของโพสต์ไม่ถูกต้อง");
        TimeFrom = string.IsNullOrWhiteSpace(TimeFrom) ? null : TimeFrom.Trim();
        TimeTo = string.IsNullOrWhiteSpace(TimeTo) ? null : TimeTo.Trim();
        if ((TimeFrom is null) != (TimeTo is null)) throw new DomainException("ช่วงเวลาของโพสต์ต้องใส่ทั้งเวลาเริ่มและเวลาสิ้นสุด");
        if (TimeFrom is not null && (!TimeOfDay.IsValid(TimeFrom) || !TimeOfDay.IsValid(TimeTo))) throw new DomainException("ช่วงเวลาของโพสต์ไม่ถูกต้อง");
        if (MaxPerDay is < 0 or > MaxPerDayLimit) throw new DomainException($"จำนวนครั้งต่อวันของโพสต์ต้องอยู่ระหว่าง 0–{MaxPerDayLimit}");
    }

    /// <summary>Does the post allow a slot at this local day and time of day (minutes after midnight)? The daily limit is checked elsewhere.</summary>
    public bool AllowsAt(DateOnly localDay, int minutes)
    {
        if (ValidFrom is { } from && localDay < from) return false;
        if (ValidUntil is { } until && localDay > until) return false;
        if (Weekdays.Count > 0 && !Weekdays.Contains((int)localDay.DayOfWeek)) return false;
        if (TimeFrom is null || TimeTo is null) return true;
        var a = TimeOfDay.Parse(TimeFrom) ?? 0;
        var b = TimeOfDay.Parse(TimeTo) ?? 24 * 60 - 1;
        return a <= b ? minutes >= a && minutes <= b : minutes >= a || minutes <= b;
    }

    /// <summary>How this post is composed: the collection's settings with this post's own hashtags, footer and position on top.</summary>
    public CollectionSettings Apply(CollectionSettings collection) => new()
    {
        Hashtags = Hashtags ?? collection.Hashtags,
        PageTags = collection.PageTags,
        Footer = Footer ?? collection.Footer,
        FooterPos = FooterPos ?? collection.FooterPos,
        Shuffle = collection.Shuffle,
        Watermark = collection.Watermark,
        WatermarkPos = collection.WatermarkPos,
        RequireApproval = collection.RequireApproval,
    };

    public CollectionPostSettings Copy() => new()
    {
        Hashtags = Hashtags, Footer = Footer, FooterPos = FooterPos, ValidFrom = ValidFrom, ValidUntil = ValidUntil,
        Weekdays = [.. Weekdays], TimeFrom = TimeFrom, TimeTo = TimeTo, MaxPerDay = MaxPerDay,
    };
}

/// <summary>A post's place in a collection (a post may sit in several collections).</summary>
public class CollectionMember
{
    public Guid CollectionId { get; private set; }
    public Guid PostId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    private CollectionMember() { } // EF Core

    public static CollectionMember Create(Guid workspaceId, Guid collectionId, Guid postId, DateTimeOffset now) =>
        new() { WorkspaceId = workspaceId, CollectionId = collectionId, PostId = postId, AddedAt = now };
}

// A master post ("คลังโพสต์"): text (spintax and {{code}} allowed) with library media, its own switch, settings and
// approval. It lives in the workspace's library on its own and may sit in any number of collections (CollectionMember);
// a schedule draws it through the collection it uses.
public class CollectionPost : Entity
{
    public const int MaxTextLength = Post.MaxContentLength;
    public const int MaxMedia = 20;
    /// <summary>Master posts per workspace.</summary>
    public const int MaxPerWorkspace = 5000;
    /// <summary>Collections one post may sit in.</summary>
    public const int MaxCollections = 50;

    public Guid WorkspaceId { get; private set; }
    public string Text { get; private set; } = "";
    public List<Guid> MediaIds { get; private set; } = [];
    public PostApproval Approval { get; private set; }
    /// <summary>Off = no schedule draws the post; the queued posts it made for the future go.</summary>
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public CollectionPostSettings Settings { get; private set; } = new();

    private CollectionPost() { } // EF Core

    /// <summary>A new post is a draft in a collection that requires approval, otherwise it is approved at once.</summary>
    public static CollectionPost Create(PostCollection collection, string text, IEnumerable<Guid>? mediaIds, DateTimeOffset now) =>
        Create(collection.WorkspaceId, text, mediaIds, collection.Settings.RequireApproval, now);

    /// <summary>A post of the library: a draft when it goes into a collection that requires approval.</summary>
    public static CollectionPost Create(Guid workspaceId, string text, IEnumerable<Guid>? mediaIds, bool requireApproval, DateTimeOffset now)
    {
        var post = new CollectionPost
        {
            WorkspaceId = workspaceId,
            Approval = requireApproval ? PostApproval.Draft : PostApproval.Approved,
            CreatedAt = now,
            UpdatedAt = now,
        };
        post.Text = Clean(text);
        post.MediaIds = CleanMedia(mediaIds);
        return post;
    }

    /// <summary>Usable in this collection: switched on, and approved when the collection requires approval.</summary>
    public bool IsUsable(PostCollection collection) => Active && (!collection.Settings.RequireApproval || Approval == PostApproval.Approved);

    /// <summary>
    /// Changes the text and media. When a collection of the post requires approval, a post that was approved or waiting for
    /// approval goes back to draft when its content changed: nobody approved the new text.
    /// </summary>
    public void Edit(string text, IEnumerable<Guid>? mediaIds, PostCollection collection, DateTimeOffset now) =>
        Edit(text, mediaIds, collection.Settings.RequireApproval, now);

    public void Edit(string text, IEnumerable<Guid>? mediaIds, bool requireApproval, DateTimeOffset now)
    {
        var t = Clean(text);
        var media = CleanMedia(mediaIds);
        var changed = t != Text || !media.SequenceEqual(MediaIds);
        Text = t;
        MediaIds = media;
        if (changed && requireApproval && Approval != PostApproval.Draft) Approval = PostApproval.Draft;
        UpdatedAt = now;
    }

    public void SetActive(bool active, DateTimeOffset now)
    {
        if (active == Active) return;
        Active = active;
        UpdatedAt = now;
    }

    /// <summary>The post's own composing and timing settings (checked here).</summary>
    public void UpdateSettings(CollectionPostSettings settings, DateTimeOffset now)
    {
        settings.Validate();
        Settings = settings;
        UpdatedAt = now;
    }

    /// <summary>How the post is composed in a collection: the collection's settings with the post's own on top.</summary>
    public CollectionSettings SettingsIn(PostCollection collection) => Settings.Apply(collection.Settings);

    /// <summary>Takes deleted library files out of the post (a deleted file must not stay attached).</summary>
    public bool DropMedia(IReadOnlySet<Guid> gone)
    {
        if (!MediaIds.Any(gone.Contains)) return false;
        MediaIds = MediaIds.Where(id => !gone.Contains(id)).ToList();
        return true;
    }

    /// <summary>The author asks for approval.</summary>
    public void RequestApproval(DateTimeOffset now)
    {
        if (Approval != PostApproval.Draft) throw new DomainException("ขออนุมัติได้เฉพาะโพสต์ที่ยังเป็นแบบร่าง");
        Approval = PostApproval.Pending;
        UpdatedAt = now;
    }

    public void Approve(DateTimeOffset now)
    {
        if (Approval != PostApproval.Pending) throw new DomainException("อนุมัติได้เฉพาะโพสต์ที่รออนุมัติ");
        Approval = PostApproval.Approved;
        UpdatedAt = now;
    }

    /// <summary>Back to draft for another round.</summary>
    public void Reject(DateTimeOffset now)
    {
        if (Approval != PostApproval.Pending) throw new DomainException("ปฏิเสธได้เฉพาะโพสต์ที่รออนุมัติ");
        Approval = PostApproval.Draft;
        UpdatedAt = now;
    }

    private static string Clean(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) throw new DomainException("กรุณาใส่ข้อความโพสต์");
        if (t.Length > MaxTextLength) throw new DomainException($"ข้อความโพสต์ยาวเกิน {MaxTextLength} ตัวอักษร");
        return t;
    }

    private static List<Guid> CleanMedia(IEnumerable<Guid>? ids)
    {
        var media = (ids ?? []).Distinct().ToList();
        if (media.Count > MaxMedia) throw new DomainException($"แนบสื่อได้ไม่เกิน {MaxMedia} ไฟล์");
        return media;
    }
}
