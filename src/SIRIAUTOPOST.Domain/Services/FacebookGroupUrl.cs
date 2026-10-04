using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>Facebook group addresses: every spelling of one group becomes the same normalised URL.</summary>
public static partial class FacebookGroupUrl
{
    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.|web\.|mbasic\.)?(?:facebook|fb)\.com/groups/([A-Za-z0-9._-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"[._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    /// <summary>The group's slug (or numeric id); null when the text is not a Facebook group address.</summary>
    public static string? Slug(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        var m = Pattern().Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"https://www.facebook.com/groups/&lt;slug&gt;", or null when the text is not a Facebook group address.</summary>
    public static string? Normalize(string? raw) => Slug(raw) is { } slug ? "https://www.facebook.com/groups/" + slug : null;

    public static bool IsValid(string? raw) => Slug(raw) is not null;

    /// <summary>A readable name from a slug: "baan.dee_shop" becomes "baan dee shop".</summary>
    public static string NameFromSlug(string slug) => Separators().Replace(slug, " ").Trim();
}
