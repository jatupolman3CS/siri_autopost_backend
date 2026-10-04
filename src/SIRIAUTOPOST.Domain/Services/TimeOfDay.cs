using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>"HH:mm" strings: the time of day a schedule posts at.</summary>
public static partial class TimeOfDay
{
    [GeneratedRegex(@"^([01]\d|2[0-3]):([0-5]\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Minutes after midnight, or null when the text is not "HH:mm" (00:00 to 23:59).</summary>
    public static int? Parse(string? hhmm)
    {
        var m = Pattern().Match((hhmm ?? "").Trim());
        return m.Success ? int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) : null;
    }

    public static bool IsValid(string? hhmm) => Parse(hhmm) is not null;

    public static string Format(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}
