using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>
/// A LINE Official Account address as the owner pastes it: <c>https://chat.line.biz/&lt;botId&gt;</c> (any page below it,
/// such as <c>/chat</c> or <c>/chat/&lt;userId&gt;</c>, names the same OA). The master list of OAs is keyed by the bot id.
/// </summary>
public static partial class LineOaUrl
{
    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?chat\.line\.biz/([A-Za-z0-9_-]{5,64})(?:[/?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string? BotId(string? raw)
    {
        var m = Pattern().Match((raw ?? "").Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string Build(string botId) => $"https://chat.line.biz/{botId.Trim()}";
}
