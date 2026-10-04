using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Entities;

// A named set of Facebook group links ("ชุดลิงก์กลุ่ม") that one browser posts to, plus other accounts that post the
// same content. A schedule pairs one with a collection.
public class LinkSet : Entity
{
    public const int MaxNameLength = 120;
    /// <summary>Link sets per workspace.</summary>
    public const int MaxPerWorkspace = 100;
    /// <summary>Links per set.</summary>
    public const int MaxLinks = 200;
    /// <summary>Other accounts per set.</summary>
    public const int MaxAccounts = 50;

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>
    /// The connected Facebook account whose browser posts the links; null = the workspace's first connected Facebook
    /// account when posts are generated.
    /// </summary>
    public Guid? PostAsAccountId { get; private set; }
    /// <summary>Other accounts of the workspace that post the same content, each to its default target (no group code).</summary>
    public List<Guid> AccountIds { get; private set; } = [];
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private LinkSet() { } // EF Core

    public static LinkSet Create(Guid workspaceId, string name, Guid? postAsAccountId, DateTimeOffset now, int sortOrder = 0)
    {
        var set = new LinkSet { WorkspaceId = workspaceId, PostAsAccountId = postAsAccountId, CreatedAt = now, SortOrder = sortOrder };
        set.Name = CleanName(name);
        return set;
    }

    public void Update(string name, Guid? postAsAccountId, IEnumerable<Guid>? accountIds)
    {
        var accounts = (accountIds ?? []).Distinct().Where(id => id != postAsAccountId).ToList();
        if (accounts.Count > MaxAccounts) throw new DomainException($"เพิ่มบัญชีอื่นได้ไม่เกิน {MaxAccounts} บัญชีต่อชุด");
        Name = CleanName(name);
        PostAsAccountId = postAsAccountId;
        AccountIds = accounts;
    }

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new DomainException("กรุณาใส่ชื่อชุดลิงก์");
        if (n.Length > MaxNameLength) throw new DomainException($"ชื่อชุดลิงก์ยาวเกิน {MaxNameLength} ตัวอักษร");
        return n;
    }
}

// One Facebook group link of a link set. Not the GroupLink value class of SocialAccount (the groups a browser synced).
public class SetLink : Entity
{
    public const int MaxNameLength = 200;
    public const int MaxUrlLength = 300;
    public const int MaxCodeLength = 100;
    public const int MaxDailyMax = 50;

    public Guid WorkspaceId { get; private set; }
    public Guid LinkSetId { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>The normalised group address; a text that is not a Facebook group address is kept as typed and is not valid.</summary>
    public string Url { get; private set; } = "";
    /// <summary>The group's code: it replaces {{code}} in the post text, or becomes its first line.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Posts per 24 hours to this group; 0 = unlimited.</summary>
    public int DailyMax { get; private set; }
    public bool Enabled { get; private set; } = true;
    public LinkHealth Health { get; private set; }
    /// <summary>Failed posts in a row.</summary>
    public int FailStreak { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SetLink() { } // EF Core

    /// <summary>A blank or invalid address is allowed (the web app adds an empty row); such a link is never used.</summary>
    public static SetLink Create(
        Guid workspaceId, Guid linkSetId, string? name, string? url, string? code, int dailyMax, DateTimeOffset now, int sortOrder = 0)
    {
        var link = new SetLink { WorkspaceId = workspaceId, LinkSetId = linkSetId, CreatedAt = now, SortOrder = sortOrder, Enabled = true, Health = LinkHealth.Ok };
        link.Apply(name, url, code, dailyMax);
        return link;
    }

    public bool IsValid => FacebookGroupUrl.IsValid(Url);

    /// <summary>Would this normalised address and code fit? (Bulk and CSV imports count a row that does not as invalid.)</summary>
    public static bool Fits(string normalizedUrl, string? code) =>
        normalizedUrl.Length <= MaxUrlLength && (code ?? "").Trim().Length <= MaxCodeLength;

    /// <summary>The group's slug, empty when the address is not valid.</summary>
    public string Slug => FacebookGroupUrl.Slug(Url) ?? "";

    /// <summary>Posts may go to this link: switched on and a real group address.</summary>
    public bool IsUsable => Enabled && IsValid;

    /// <summary>The form: values and the switch. Switching off by hand or on again follows <see cref="DisableManually"/> / <see cref="Enable"/>.</summary>
    public void Edit(string? name, string? url, string? code, int dailyMax, bool enabled)
    {
        Apply(name, url, code, dailyMax);
        if (enabled != Enabled)
        {
            if (enabled) Enable();
            else DisableManually();
        }
    }

    /// <summary>Too many failures in a row: the engine switched it off.</summary>
    public void AutoDisable(int failStreak)
    {
        Enabled = false;
        Health = LinkHealth.Off;
        FailStreak = Math.Max(0, failStreak);
    }

    public void DisableManually()
    {
        Enabled = false;
        Health = LinkHealth.Off;
        FailStreak = 0;
    }

    /// <summary>Switched on again (after an automatic or manual switch-off).</summary>
    public void Enable()
    {
        Enabled = true;
        Health = LinkHealth.Ok;
        FailStreak = 0;
    }

    /// <summary>A post went out. A link that is switched off stays off.</summary>
    public void RecordSuccess()
    {
        FailStreak = 0;
        if (Health != LinkHealth.Off) Health = LinkHealth.Ok;
    }

    public void RecordFailure() => FailStreak++;

    /// <summary>The group holds the post for admin approval.</summary>
    public void RecordPendingApproval()
    {
        if (Health != LinkHealth.Off) Health = LinkHealth.Pending;
    }

    private void Apply(string? name, string? url, string? code, int dailyMax)
    {
        var raw = (url ?? "").Trim();
        var normalized = FacebookGroupUrl.Normalize(raw);
        var u = normalized ?? raw;
        var c = (code ?? "").Trim();
        var n = (name ?? "").Trim();
        if (n.Length == 0 && normalized is not null)
        {
            // A name made from the address is cut to fit rather than refused: the person did not type it.
            n = FacebookGroupUrl.NameFromSlug(FacebookGroupUrl.Slug(normalized)!);
            if (n.Length > MaxNameLength) n = n[..MaxNameLength];
        }
        if (u.Length > MaxUrlLength) throw new DomainException($"ลิงก์ยาวเกิน {MaxUrlLength} ตัวอักษร");
        if (n.Length > MaxNameLength) throw new DomainException($"ชื่อกลุ่มยาวเกิน {MaxNameLength} ตัวอักษร");
        if (c.Length > MaxCodeLength) throw new DomainException($"รหัสกลุ่มยาวเกิน {MaxCodeLength} ตัวอักษร");
        if (dailyMax is < 0 or > MaxDailyMax) throw new DomainException($"เพดานต่อวันของกลุ่มต้องอยู่ระหว่าง 0–{MaxDailyMax}");
        Url = u;
        Name = n;
        Code = c;
        DailyMax = dailyMax;
    }
}
