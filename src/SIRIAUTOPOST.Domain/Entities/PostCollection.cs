using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

/// <summary>How a collection writes its posts, kept as one jsonb document on the collection.</summary>
public class CollectionSettings
{
    public const int MaxHashtagsLength = 500;
    public const int MaxPageTagsLength = 1000;
    public const int MaxFooterLength = 1000;

    public string Hashtags { get; set; } = "";
    /// <summary>Stored only: lines "name | url" of pages to tag; nothing posts them yet.</summary>
    public string PageTags { get; set; } = "";
    public string Footer { get; set; } = "";
    public FooterPosition FooterPos { get; set; } = FooterPosition.End;
    /// <summary>Stored only: "shuffle image order"; the extension ignores it.</summary>
    public bool Shuffle { get; set; } = true;
    /// <summary>Stored only: the extension does not draw a watermark yet.</summary>
    public bool Watermark { get; set; }
    public WatermarkPosition WatermarkPos { get; set; } = WatermarkPosition.Br;
    /// <summary>Only approved posts of the collection are posted.</summary>
    public bool RequireApproval { get; set; }

    public void Validate()
    {
        Hashtags = (Hashtags ?? "").Trim();
        PageTags = (PageTags ?? "").Trim();
        Footer = (Footer ?? "").Trim();
        if (Hashtags.Length > MaxHashtagsLength) throw new DomainException($"แฮชแท็กยาวเกิน {MaxHashtagsLength} ตัวอักษร");
        if (PageTags.Length > MaxPageTagsLength) throw new DomainException($"รายการแท็กเพจยาวเกิน {MaxPageTagsLength} ตัวอักษร");
        if (Footer.Length > MaxFooterLength) throw new DomainException($"ข้อความส่วนท้ายยาวเกิน {MaxFooterLength} ตัวอักษร");
        if (!Enum.IsDefined(FooterPos) || !Enum.IsDefined(WatermarkPos)) throw new DomainException("ค่าที่ส่งมาไม่ถูกต้อง");
    }
}

// A named set of library posts with composing settings ("ชุดโพสต์"). A schedule pairs one with a link set.
public class PostCollection : Entity
{
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 300;
    public const int MaxIconLength = 40;
    public const string DefaultIcon = "ph-folder";
    /// <summary>Collections per workspace.</summary>
    public const int MaxPerWorkspace = 100;

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Icon { get; private set; } = DefaultIcon;
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public CollectionSettings Settings { get; private set; } = new();

    private PostCollection() { } // EF Core

    public static PostCollection Create(Guid workspaceId, string name, string? description, DateTimeOffset now, int sortOrder = 0)
    {
        var c = new PostCollection { WorkspaceId = workspaceId, CreatedAt = now, SortOrder = sortOrder };
        c.Rename(name, description);
        return c;
    }

    /// <summary>Changes the name and description; the description may be empty.</summary>
    public void Rename(string? name, string? description)
    {
        var n = (name ?? "").Trim();
        var d = (description ?? "").Trim();
        if (n.Length == 0) throw new DomainException("กรุณาใส่ชื่อชุดโพสต์");
        if (n.Length > MaxNameLength) throw new DomainException($"ชื่อชุดโพสต์ยาวเกิน {MaxNameLength} ตัวอักษร");
        if (d.Length > MaxDescriptionLength) throw new DomainException($"คำอธิบายยาวเกิน {MaxDescriptionLength} ตัวอักษร");
        Name = n;
        Description = d;
    }

    /// <summary>The edit form: name, description, an optional icon (null keeps it) and the composing settings.</summary>
    public void Update(string? name, string? description, string? icon, CollectionSettings settings)
    {
        Rename(name, description);
        var i = icon?.Trim();
        if (i is { Length: > MaxIconLength }) throw new DomainException($"ไอคอนยาวเกิน {MaxIconLength} ตัวอักษร");
        if (!string.IsNullOrEmpty(i)) Icon = i;
        settings.Validate();
        Settings = settings;
    }

    /// <summary>
    /// Posts of the collection that may be scheduled: all of them, or only the approved ones when the collection
    /// requires approval. Turning approval on does not touch posts that were approved before.
    /// </summary>
    public bool CanUse(CollectionPost post) => post.IsUsable(this);
}

// One post of a collection: text (spintax and {{code}} allowed) with library media, and where it is in approval.
public class CollectionPost : Entity
{
    public const int MaxTextLength = Post.MaxContentLength;
    public const int MaxMedia = 20;
    /// <summary>Collection posts per workspace.</summary>
    public const int MaxPerWorkspace = 5000;

    public Guid WorkspaceId { get; private set; }
    public Guid CollectionId { get; private set; }
    public string Text { get; private set; } = "";
    public List<Guid> MediaIds { get; private set; } = [];
    public PostApproval Approval { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private CollectionPost() { } // EF Core

    /// <summary>A new post is a draft in a collection that requires approval, otherwise it is approved at once.</summary>
    public static CollectionPost Create(PostCollection collection, string text, IEnumerable<Guid>? mediaIds, DateTimeOffset now)
    {
        var post = new CollectionPost
        {
            WorkspaceId = collection.WorkspaceId,
            CollectionId = collection.Id,
            Approval = collection.Settings.RequireApproval ? PostApproval.Draft : PostApproval.Approved,
            CreatedAt = now,
            UpdatedAt = now,
        };
        post.Text = Clean(text);
        post.MediaIds = CleanMedia(mediaIds);
        return post;
    }

    public bool IsUsable(PostCollection collection) => !collection.Settings.RequireApproval || Approval == PostApproval.Approved;

    /// <summary>
    /// Changes the text and media. In a collection that requires approval, a post that was approved or waiting for
    /// approval goes back to draft when its content changed: nobody approved the new text.
    /// </summary>
    public void Edit(string text, IEnumerable<Guid>? mediaIds, PostCollection collection, DateTimeOffset now)
    {
        var t = Clean(text);
        var media = CleanMedia(mediaIds);
        var changed = t != Text || !media.SequenceEqual(MediaIds);
        Text = t;
        MediaIds = media;
        if (changed && collection.Settings.RequireApproval && Approval != PostApproval.Draft) Approval = PostApproval.Draft;
        UpdatedAt = now;
    }

    /// <summary>Moves the post to another collection. A collection that requires approval has to approve it itself.</summary>
    public void Move(PostCollection target, DateTimeOffset now)
    {
        if (target.WorkspaceId != WorkspaceId) throw new DomainException("ชุดโพสต์ปลายทางไม่ได้อยู่ในเวิร์กสเปซนี้");
        if (target.Id == CollectionId) return;
        CollectionId = target.Id;
        if (target.Settings.RequireApproval) Approval = PostApproval.Draft;
        UpdatedAt = now;
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
