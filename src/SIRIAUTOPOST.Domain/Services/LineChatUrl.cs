using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>
/// LINE OA customer chat addresses: <c>https://chat.line.biz/&lt;botId&gt;/chat/&lt;userId&gt;</c>.
/// Compares case-insensitively. Normalised URL retains the typed case of botId and userId.
/// </summary>
public static partial class LineChatUrl
{
    private const string ChatPatternString = @"^(?:https?://)?(?:www\.)?chat\.line\.biz/([A-Za-z0-9_-]{5,64})/chat/([A-Za-z0-9_-]{10,64})(?:[/?#].*)?$";

    [GeneratedRegex(ChatPatternString, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPattern();

    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool Same(string? a, string? b) => Comparer.Equals(a, b);

    public static (string BotId, string UserId, string Url)? Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;

        var m = ChatPattern().Match(s);
        if (!m.Success) return null;

        var botId = m.Groups[1].Value;
        var userId = m.Groups[2].Value;
        return (botId, userId, $"https://chat.line.biz/{botId}/chat/{userId}");
    }

    public static string Build(string botId, string userId) => $"https://chat.line.biz/{botId.Trim()}/chat/{userId.Trim()}";

    public static string? Normalize(string? raw) => Parse(raw)?.Url;

    public static bool IsValid(string? raw) => Parse(raw) is not null;

    public static string? BotId(string? raw) => Parse(raw)?.BotId;

    public static string? UserId(string? raw) => Parse(raw)?.UserId;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserIdPattern();

    /// <summary>A customer id as the address carries it (assumption A1 of the spec: one place to tighten).</summary>
    public static bool IsValidUserId(string? userId) => userId is not null && UserIdPattern().IsMatch(userId);
}
