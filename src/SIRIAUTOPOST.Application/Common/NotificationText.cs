using System.Globalization;
using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// The markup of a notification. Every text handed to <see cref="Interfaces.INotificationDispatcher"/> is Telegram HTML
/// (only <c>&lt;b&gt;</c> and the three escapes <c>&amp;amp; &amp;lt; &amp;gt;</c>); LINE gets <see cref="ToPlain"/> of it.
/// </summary>
public static partial class NotificationText
{
    /// <summary>The calendar of a message about something without a schedule (a test post): Thailand's, UTC+7.</summary>
    public const int DefaultUtcOffsetMinutes = 420;

    /// <summary>Telegram refuses a photo caption longer than this (counted after the markup is read).</summary>
    public const int TelegramCaptionMax = 1024;

    /// <summary>Makes a person's text (a group name, an error) safe to put in a notification.</summary>
    public static string Esc(string? text) =>
        (text ?? "").Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public static string Bold(string? text) => $"<b>{Esc(text)}</b>";

    [GeneratedRegex("</?b>")]
    private static partial Regex Tags();

    /// <summary>The text without markup: what a channel that has no bold shows.</summary>
    public static string ToPlain(string? html) =>
        Tags().Replace(html ?? "", "").Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>"03/10 16:41:35", the way the extension has always written a time, in the calendar <paramref name="utcOffsetMinutes"/> ahead of UTC.</summary>
    public static string Time(DateTimeOffset at, int utcOffsetMinutes) =>
        at.ToOffset(TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -840, 840))).ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture);
}
