using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Entities;

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

    public void MarkHealthy() => Health = AccountHealth.Ok;
}
