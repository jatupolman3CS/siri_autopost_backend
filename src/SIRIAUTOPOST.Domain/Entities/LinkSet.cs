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
    /// <summary>
    /// RETIRED (2026-10-10): "other accounts that post the same content through their own extension" broke the rule that a
    /// link set is posted by ONE extension. It is always empty now; <see cref="Update"/> drops what it is given, the API
    /// refuses a non-empty list, and the start-up pass clears what older data held.
    /// </summary>
    public List<Guid> AccountIds { get; private set; } = [];
    public int SortOrder { get; private set; }
    /// <summary>Off = schedules that use the set queue nothing to it until it is switched on again.</summary>
    public bool Active { get; private set; } = true;
    /// <summary>
    /// The groups of a round go out in a random order (a different one every round, and a round never opens with the group
    /// the round before it opened with), so no group is always the first to be posted. Off = the order of the list. The
    /// workspace's anti-ban "shuffle the order of target groups" must be on as well.
    /// </summary>
    public bool RandomOrder { get; private set; } = true;
    /// <summary>
    /// How many posts a day each group of the set may get (the schedule's local day); 0 = no limit from the set. A link's own
    /// <see cref="SetLink.DailyMax"/> replaces it for that link. A group that has more times than this in a day gets a random
    /// choice of them, so the same group is not always posted in the first slot.
    /// </summary>
    public int DailyPerGroup { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private LinkSet() { } // EF Core

    public static LinkSet Create(Guid workspaceId, string name, Guid? postAsAccountId, DateTimeOffset now, int sortOrder = 0)
    {
        var set = new LinkSet { WorkspaceId = workspaceId, PostAsAccountId = postAsAccountId, CreatedAt = now, SortOrder = sortOrder };
        set.Name = CleanName(name);
        return set;
    }

    public void SetActive(bool active) => Active = active;

    /// <param name="randomOrder">Null keeps the stored value.</param>
    /// <param name="dailyPerGroup">Null keeps the stored value.</param>
    public void Update(string name, Guid? postAsAccountId, IEnumerable<Guid>? accountIds, bool? randomOrder = null, int? dailyPerGroup = null)
    {
        if (dailyPerGroup is < 0 or > SetLink.MaxDailyMax)
            throw new DomainException($"จำนวนโพสต์ต่อวันต่อกลุ่มของชุดต้องอยู่ระหว่าง 0–{SetLink.MaxDailyMax}");
        Name = CleanName(name);
        PostAsAccountId = postAsAccountId;
        AccountIds = []; // retired: see the property
        RandomOrder = randomOrder ?? RandomOrder;
        DailyPerGroup = dailyPerGroup ?? DailyPerGroup;
    }

    /// <summary>
    /// The most posts a day this link of the set may get: its own limit when it has one, else the set's; 0 = no limit.
    /// </summary>
    public int DailyCapFor(SetLink link) => link.DailyMax > 0 ? link.DailyMax : DailyPerGroup;

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
