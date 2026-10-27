using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>A link that keeps a target address for the extension (account) that posts its set.</summary>
public sealed record GroupHolder(LinkSet Set, SetLink Link, Guid Owner);

/// <summary>
/// Which extension a target address belongs to (2026-10-10). The rule: a link set is posted by ONE extension, and one
/// target is in the sets of ONE extension. Two extensions never post to the same target, so their posts cannot mix, and
/// no extension picks up another's targets. The map answers, for an address, which extensions hold it (a usable link
/// in a set that has an owner); the writes (<see cref="EnsureFree"/>) refuse to add a second extension, and the claim
/// asks <see cref="WinnerOf"/> so data from before the rule (the same target in two extensions' sets) still posts from
/// one extension only: the one that comes first in the accounts' own order (sort order, then name: the order the team
/// page and the link-set card list them in, so the web app can show who keeps the target without asking the server).
/// </summary>
public sealed class OwnershipMap
{
    private sealed class TargetKeyComparer : IEqualityComparer<(Platform Channel, string Url)>
    {
        public static readonly TargetKeyComparer Instance = new();
        public bool Equals((Platform Channel, string Url) x, (Platform Channel, string Url) y) =>
            x.Channel == y.Channel && StringComparer.OrdinalIgnoreCase.Equals(x.Url, y.Url);
        public int GetHashCode((Platform Channel, string Url) obj) =>
            HashCode.Combine(obj.Channel, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Url));
    }

    private readonly Dictionary<(Platform Channel, string Url), List<GroupHolder>> byChannelAndUrl = new(TargetKeyComparer.Instance);

    private OwnershipMap() { }

    private static Platform DetectChannel(string url) =>
        LineChatUrl.IsValid(url) ? Platform.Line : Platform.Fb;

    /// <summary>Every usable link of a set that has an owner, by address. Switched-off links and sets nobody posts hold nothing.</summary>
    public static OwnershipMap Build(IReadOnlyList<LinkSet> sets, IReadOnlyList<SetLink> links, IReadOnlyList<SocialAccount> accounts)
    {
        var map = new OwnershipMap();
        var owners = sets.ToDictionary(s => s.Id, s => LinkOwnership.OwnerOf(s, accounts));
        var setById = sets.ToDictionary(s => s.Id);
        var rank = accounts.Select((a, i) => (a.Id, i)).ToDictionary(x => x.Id, x => x.i);
        foreach (var link in links.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id))
        {
            if (!link.IsUsable || !setById.TryGetValue(link.LinkSetId, out var set) || owners[set.Id] is not { } owner) continue;
            var channel = set.Channel;
            var normalizedUrl = LinkAddress.Normalize(channel, link.Url) ?? link.Url;
            var key = (channel, normalizedUrl);
            if (!map.byChannelAndUrl.TryGetValue(key, out var list)) map.byChannelAndUrl[key] = list = [];
            list.Add(new GroupHolder(set, link, owner));
        }
        // The holder that comes first keeps the target: the extension first in the accounts' order, then the oldest link.
        foreach (var (key, list) in map.byChannelAndUrl.ToList())
            map.byChannelAndUrl[key] = list.OrderBy(h => rank.GetValueOrDefault(h.Owner, int.MaxValue)).ToList(); // stable: ties stay oldest first
        return map;
    }

    /// <summary>
    /// The extension that keeps the target when more than one holds it: the first in the accounts' order. Null when
    /// nobody holds it (the address is in no set that has an owner).
    /// </summary>
    public GroupHolder? WinnerOf(string url, Platform? channel = null)
    {
        var ch = channel ?? DetectChannel(url);
        var normalized = LinkAddress.Normalize(ch, url) ?? url;
        return byChannelAndUrl.TryGetValue((ch, normalized), out var list) ? list[0] : null;
    }

    /// <summary>A holder of the target in a set other than <paramref name="exceptSetId"/> that belongs to another extension than <paramref name="owner"/>.</summary>
    public GroupHolder? ConflictFor(string url, Guid owner, Guid exceptSetId, Platform? channel = null)
    {
        var ch = channel ?? DetectChannel(url);
        var normalized = LinkAddress.Normalize(ch, url) ?? url;
        return byChannelAndUrl.TryGetValue((ch, normalized), out var list)
            ? list.FirstOrDefault(h => h.Set.Id != exceptSetId && h.Owner != owner)
            : null;
    }

    /// <summary>
    /// Refuses (422, in Thai, naming the target, the set and the extension) when a target is already in a set of another
    /// extension. Null owner = a set nobody posts yet: nothing to compare until a person picks the extension.
    /// </summary>
    public void EnsureFree(string url, Guid? owner, Guid setId, IReadOnlyList<SocialAccount> accounts, Platform? channel = null)
    {
        if (owner is not { } o || ConflictFor(url, o, setId, channel) is not { } held) return;
        throw new DomainException(Refusal(url, held, accounts));
    }

    /// <summary>The sentence a person reads when a target is already taken by another extension.</summary>
    public static string Refusal(string url, GroupHolder held, IReadOnlyList<SocialAccount> accounts) =>
        held.Set.Channel == Platform.Line
            ? $"แชท {held.Link.Name ?? LineChatUrl.UserId(url) ?? url} อยู่ในชุดลิงก์ \"{held.Set.Name}\" ของเครื่อง \"{ExtensionName(held.Owner, accounts)}\" แล้ว " +
              "แชทหนึ่งใช้ได้กับเครื่องเดียว นำออกจากชุดนั้นก่อน หรือย้ายชุดไปเครื่องเดียวกัน"
            : $"กลุ่ม {FacebookGroupUrl.NameFromSlug(FacebookGroupUrl.Slug(url) ?? url)} อยู่ในชุดลิงก์ \"{held.Set.Name}\" ของเครื่อง \"{ExtensionName(held.Owner, accounts)}\" แล้ว " +
              "กลุ่มหนึ่งใช้ได้กับเครื่องเดียว นำออกจากชุดนั้นก่อน หรือย้ายชุดไปเครื่องเดียวกัน";

    /// <summary>What a person calls the extension: the browser's name ("Shop PC"), not the account's "Facebook · Shop PC" or "LINE · Shop PC".</summary>
    public static string ExtensionName(Guid accountId, IReadOnlyList<SocialAccount> accounts)
    {
        var name = accounts.FirstOrDefault(a => a.Id == accountId)?.Name;
        if (name is null) return "ที่ถูกลบไปแล้ว";
        const string fbPrefix = "Facebook · ";
        if (name.StartsWith(fbPrefix, StringComparison.Ordinal)) return name[fbPrefix.Length..];
        const string linePrefix = "LINE · ";
        if (name.StartsWith(linePrefix, StringComparison.Ordinal)) return name[linePrefix.Length..];
        return name;
    }
}

public static class LinkOwnership
{
    /// <summary>
    /// The extension (account id) that posts the set: the one <see cref="LinkSetAccounts.PostingAccount"/> picks, else the
    /// account the set names even when its browser is unbound (the set waits for it). Null = the set belongs to nobody
    /// yet (several extensions and no choice, or none connected and no choice).
    /// </summary>
    public static Guid? OwnerOf(LinkSet set, IReadOnlyList<SocialAccount> accounts) =>
        LinkSetAccounts.PostingAccount(set, accounts)?.Id ?? set.PostAsAccountId;

    /// <summary>
    /// Gives the sets that name no extension to <paramref name="owner"/>, the one extension that was posting them. Called
    /// when a second extension pairs (before it, "no choice" meant the only one), so adding a machine does not leave the
    /// sets of the first one without an owner. Returns the sets changed.
    /// </summary>
    public static IReadOnlyList<LinkSet> PinUnassigned(IEnumerable<LinkSet> sets, Guid owner, Platform platform = Platform.Fb)
    {
        var pinned = new List<LinkSet>();
        foreach (var set in sets.Where(s => s.PostAsAccountId is null && s.Channel == platform))
        {
            set.Update(set.Name, owner, set.AccountIds);
            pinned.Add(set);
        }
        return pinned;
    }

    /// <summary>The only connected account of the given platform, else null.</summary>
    public static SocialAccount? OnlyConnected(IReadOnlyList<SocialAccount> accounts, Platform platform = Platform.Fb)
    {
        var connected = accounts.Where(a => a.Platform == platform && a.IsConnected).ToList();
        return connected.Count == 1 ? connected[0] : null;
    }
}
