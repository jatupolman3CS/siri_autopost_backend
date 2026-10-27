using System.Globalization;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Services;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// How far apart a schedule puts two groups (2026-10-13, "how many minutes per group"). A schedule with a
/// <see cref="Schedule.GroupGapMinutes"/> keeps at least that many minutes between two posts (plus a little human jitter on
/// top, never less); one without uses the workspace's smart delay, a random gap between its Min and Max. The materializer
/// uses <see cref="GapSeconds"/> for new posts and <see cref="Reflow"/> lays out again what is already queued.
/// </summary>
public static class ScheduleSpacing
{
    /// <summary>One gap in seconds, whole seconds (the database keeps microseconds and a time should read back as it was made).</summary>
    public static double GapSeconds(Schedule s, AntiBanSettings anti, Func<double> next, Platform channel = Platform.Fb) =>
        s.GroupGapMinutes > 0
            ? Math.Round(s.GroupGapMinutes * 60 * (1 + next() * Schedule.GroupGapJitter))
            : channel == Platform.Line
                ? Math.Round(anti.LineChat.MinGapSeconds + next() * (anti.LineChat.MaxGapSeconds - anti.LineChat.MinGapSeconds))
                : Math.Round((anti.Min + next() * (anti.Max - anti.Min)) * 60);

    /// <summary>
    /// Lays out the posts a schedule still has queued again, with its gap: the rounds of one local day follow each other on
    /// one line (a post is at its slot's time or one gap after the one before it, whichever is later), in the order they had.
    /// For today the line starts after "now" and one gap after the moment the browser last took a post, so a backlog
    /// of due posts becomes a spread plan instead of a burst. Days that are over, posts somebody asked to post now, posts
    /// waiting for the extension and posts that are not a schedule's are left alone. Returns the posts that moved.
    /// </summary>
    /// <param name="lastBegan">When the browser that posts the schedule last took a post; null = never or unknown.</param>
    /// <param name="gapSeconds">One gap, in seconds (see <see cref="GapSeconds"/>).</param>
    public static int Reflow(
        Schedule s, IEnumerable<Post> posts, DateTimeOffset now, DateTimeOffset? lastBegan, Func<double> gapSeconds)
    {
        var today = s.LocalDay(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var queued = posts
            .Where(p => p.ScheduleId == s.Id && p.Status == PostStatus.Queued && p.RushedAt is null && !p.IsTest && p.SlotKey is { Length: >= 10 } && p.DeferCount == 0)
            .ToList();
        var moved = 0;
        foreach (var day in queued.GroupBy(p => p.SlotKey![..10]).Where(g => string.CompareOrdinal(g.Key, today) >= 0)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Today starts after this moment (a few seconds, so the first one is not instant) and one gap after the last post taken.
            DateTimeOffset? end = null;
            DateTimeOffset floor = DateTimeOffset.MinValue;
            if (day.Key == today)
            {
                floor = now.AddSeconds(5);
                if (lastBegan is { } began) floor = Latest(floor, began.AddSeconds(gapSeconds()));
            }
            // The rounds in the order they open: a regular round at its time, the start-now round (no time of its own) at its
            // earliest post, so that round, which began at the click, stays ahead of the day's regular times.
            var opens = day.GroupBy(p => p.SlotKey!).ToDictionary(g => g.Key, g => SlotStart(s, g.First()) ?? g.Min(x => x.ScheduledAt));
            foreach (var p in day.OrderBy(p => opens[p.SlotKey!]).ThenBy(p => p.SlotKey, StringComparer.Ordinal).ThenBy(p => p.ScheduledAt).ThenBy(p => p.Id))
            {
                var at = Latest(SlotStart(s, p) ?? p.ScheduledAt, floor);
                if (end is { } e) at = Latest(at, e.AddSeconds(gapSeconds()));
                if (s.LineOptions is not null && !s.AllowsLineAt(at))
                {
                    var localAt = at.ToOffset(TimeSpan.FromMinutes(s.UtcOffsetMinutes));
                    var nextDay = DateOnly.FromDateTime(localAt.DateTime).AddDays(1);
                    at = s.ToUtc(nextDay, s.LineOptions.AllowedFrom);
                }
                if (at.ToUniversalTime() != p.ScheduledAt)
                {
                    p.Reschedule(at, now);
                    moved++;
                }
                end = at;
            }
        }
        return moved;
    }

    /// <summary>The instant a post's slot opens ("yyyy-MM-ddTHH:mm" in the schedule's calendar); null for the start-now round and anything odd.</summary>
    private static DateTimeOffset? SlotStart(Schedule s, Post p)
    {
        var key = p.SlotKey!;
        if (key.Length < 16 || key[10] != 'T') return null;
        if (!DateOnly.TryParseExact(key[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
        return TimeOfDay.Parse(key[11..]) is null ? null : s.ToUtc(day, key[11..]);
    }

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
