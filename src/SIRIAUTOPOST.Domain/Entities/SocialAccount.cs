using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Entities;

public class GroupLink
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

// A social account the extension posts with. Accounts that post to Facebook groups list those groups.
public class SocialAccount : Entity
{
    public Guid WorkspaceId { get; private set; }
    public Platform Platform { get; private set; }
    public string Name { get; private set; } = "";
    public string Handle { get; private set; } = "";
    /// <summary>Where a post goes when the account has no groups (Feed, Timeline, Broadcast...).</summary>
    public string DefaultTarget { get; private set; } = "";
    public AccountHealth Health { get; private set; }
    public List<string> Groups { get; private set; } = [];
    /// <summary>Group links reported by the extension; the extension posts to these URLs.</summary>
    public List<GroupLink> GroupLinks { get; private set; } = [];
    /// <summary>The device whose browser holds this account's login, when connected through the extension.</summary>
    public Guid? DeviceId { get; private set; }
    public int SortOrder { get; private set; }

    private SocialAccount() { } // EF Core

    public static SocialAccount Create(
        Guid workspaceId, Platform platform, string name, string handle, string defaultTarget,
        AccountHealth health = AccountHealth.Ok, IEnumerable<string>? groups = null, int sortOrder = 0) =>
        new()
        {
            WorkspaceId = workspaceId,
            Platform = platform,
            Name = name,
            Handle = handle,
            DefaultTarget = defaultTarget,
            Health = health,
            Groups = groups?.ToList() ?? [],
            SortOrder = sortOrder,
        };

    public bool PostsToGroups => Groups.Count > 0;

    public bool CanPost => Health != AccountHealth.Relogin;

    public bool IsConnected => DeviceId is not null;

    public void MarkHealthy() => Health = AccountHealth.Ok;

    /// <summary>The extension found Facebook logged out or asking for a checkpoint.</summary>
    public void MarkNeedsLogin() => Health = AccountHealth.Relogin;

    /// <summary>The Facebook account of a newly paired browser.</summary>
    public static SocialAccount Connect(Guid workspaceId, Device device, int sortOrder) =>
        new()
        {
            WorkspaceId = workspaceId,
            Platform = Platform.Fb,
            Name = $"Facebook · {device.Name}",
            Handle = "เชื่อมผ่านส่วนขยาย",
            DefaultTarget = "กลุ่ม Facebook",
            Health = AccountHealth.Ok,
            DeviceId = device.Id,
            SortOrder = sortOrder,
        };

    /// <summary>Follows a rename of its browser in the web app.</summary>
    public void FollowDevice(Device device) => Name = $"Facebook · {device.Name}";

    /// <summary>Replaces the groups with the ones the extension knows (names must be unique).</summary>
    public void SyncGroups(IEnumerable<GroupLink> links)
    {
        var list = links
            .Select(l => new GroupLink { Name = (l.Name ?? "").Trim(), Url = (l.Url ?? "").Trim() })
            .Where(l => l.Url.Length > 0)
            .Select(l => l.Name.Length > 0 ? l : new GroupLink { Name = l.Url, Url = l.Url })
            .DistinctBy(l => l.Name)
            .ToList();
        GroupLinks = list;
        Groups = list.Select(l => l.Name).ToList();
    }

    public string? UrlFor(string group) => GroupLinks.FirstOrDefault(l => l.Name == group)?.Url;

    /// <summary>The device was unbound: the account stays (with its history) but cannot post.</summary>
    public void Disconnect()
    {
        DeviceId = null;
        Health = AccountHealth.Relogin;
    }
}
