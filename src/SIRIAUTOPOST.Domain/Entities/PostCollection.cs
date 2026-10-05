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
    /// <summary>Off = schedules that use the collection queue nothing from it until it is switched on again.</summary>
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public CollectionSettings Settings { get; private set; } = new();

    private PostCollection() { } // EF Core

    public static PostCollection Create(Guid workspaceId, string name, string? description, DateTimeOffset now, int sortOrder = 0)
    {
        var c = new PostCollection { WorkspaceId = workspaceId, CreatedAt = now, SortOrder = sortOrder };
        c.Rename(name, description);
        return c;
    }

    public void SetActive(bool active) => Active = active;

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
