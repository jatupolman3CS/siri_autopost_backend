using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>What a Facebook address points to. Groups and pages are the places the system posts to for now.</summary>
public enum FacebookTargetKind
{
    Group,
    Page,
}

/// <summary>
/// Facebook group and page addresses: every spelling of one group or page becomes the same normalised URL. (The name is
/// from when only groups were supported; the class decides for both.) A group is <c>/groups/&lt;slug&gt;</c>; a page is
/// a vanity address (<c>facebook.com/baandee.shop</c>), <c>profile.php?id=&lt;id&gt;</c>, <c>/pages/&lt;name&gt;/&lt;id&gt;</c> or
/// <c>/p/&lt;name-id&gt;</c>. Reserved first segments (watch, marketplace, events, login...) are never a page.
/// </summary>
public static partial class FacebookGroupUrl
{
    private const string Host = @"^(?:https?://)?(?:www\.|m\.|web\.|mbasic\.)?(?:facebook|fb)\.com";

    [GeneratedRegex(Host + @"/groups/([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GroupPattern();

    [GeneratedRegex(Host + @"/profile\.php\?(?:[^#]*&)?id=(\d{5,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();

    [GeneratedRegex(Host + @"/pages/([^/?#\s]+)/(\d{5,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PagesPattern();

    [GeneratedRegex(Host + @"/p/([A-Za-z0-9._%-]+)(?=[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedPattern();

    [GeneratedRegex(Host + @"/([A-Za-z0-9.]{5,})(?=[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VanityPattern();

    [GeneratedRegex(@"[._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    /// <summary>First path segments that are Facebook's own screens, not a page.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "groups", "pages", "watch", "marketplace", "events", "share", "sharer", "reel", "reels", "stories", "story.php", "photo",
        "photos", "photo.php", "video", "videos", "login", "login.php", "home.php", "settings", "help", "policies", "privacy", "ads",
        "business", "gaming", "people", "permalink.php", "hashtag", "search", "friends", "messages", "notifications", "bookmarks",
        "fundraisers", "jobs", "offers", "public", "dialog", "plugins", "profile.php", "checkpoint", "recover", "r.php", "l.php",
        "composer", "feeds", "saved", "memories", "campaign", "careers", "directory", "legal", "about", "support", "tr", "flx",
    };

    /// <summary>
    /// Compares addresses the way Facebook does: <c>/groups/ABC</c> and <c>/groups/abc</c> are one group. A link keeps its
    /// slug as it was typed (for display), but whether two links are the same group is always decided with this.
    /// </summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool Same(string? a, string? b) => Comparer.Equals(a, b);

    /// <summary>The kind and the readable id of an address (a slug, a name or a number), or null when it is neither a group nor a page.</summary>
    private static (FacebookTargetKind Kind, string Slug, string Url)? Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;

        var m = GroupPattern().Match(s);
        if (m.Success)
        {
            var slug = m.Groups[1].Value;
            // A slug needs at least one letter or digit: "." and ".." (and "-", "_") are not groups.
            return slug.Any(char.IsAsciiLetterOrDigit) ? (FacebookTargetKind.Group, slug, "https://www.facebook.com/groups/" + slug) : null;
        }
        m = ProfileIdPattern().Match(s);
        if (m.Success) return (FacebookTargetKind.Page, m.Groups[1].Value, "https://www.facebook.com/profile.php?id=" + m.Groups[1].Value);
        m = PagesPattern().Match(s);
        if (m.Success) return (FacebookTargetKind.Page, m.Groups[1].Value, $"https://www.facebook.com/pages/{m.Groups[1].Value}/{m.Groups[2].Value}");
        m = PrefixedPattern().Match(s);
        if (m.Success && m.Groups[1].Value.Any(char.IsAsciiLetterOrDigit))
            return (FacebookTargetKind.Page, m.Groups[1].Value, "https://www.facebook.com/p/" + m.Groups[1].Value);
        m = VanityPattern().Match(s);
        if (m.Success && !Reserved.Contains(m.Groups[1].Value) && m.Groups[1].Value.Any(char.IsAsciiLetter))
            return (FacebookTargetKind.Page, m.Groups[1].Value, "https://www.facebook.com/" + m.Groups[1].Value);
        return null;
    }

    /// <summary>The group's slug (or numeric id) or the page's name; null when the text is not a Facebook group or page address.</summary>
    public static string? Slug(string? raw) => Parse(raw)?.Slug;

    /// <summary>Group or page; null when the text is neither.</summary>
    public static FacebookTargetKind? KindOf(string? raw) => Parse(raw)?.Kind;

    /// <summary>"https://www.facebook.com/groups/&lt;slug&gt;" or the page's address; null when the text is neither.</summary>
    public static string? Normalize(string? raw) => Parse(raw)?.Url;

    public static bool IsValid(string? raw) => Parse(raw) is not null;

    /// <summary>A readable name from a slug: "baan.dee_shop" becomes "baan dee shop".</summary>
    public static string NameFromSlug(string slug) => Separators().Replace(slug, " ").Trim();
}
