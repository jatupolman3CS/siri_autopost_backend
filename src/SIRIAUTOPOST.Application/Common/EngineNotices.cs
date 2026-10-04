using System.Text.RegularExpressions;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>One message for the notification system.</summary>
public sealed record Notice(NotifyEvent Event, Guid? LinkSetId, Guid? LinkId, string Text);

/// <summary>
/// What the posting engine tells people (Telegram, LINE) and the Thai wording of it. Messages are collected while a
/// claim or a result is handled and sent after the changes are saved; a notification that fails never fails the call.
/// </summary>
public static partial class EngineNotices
{
    public static async Task SendAsync(INotificationDispatcher dispatcher, Guid workspaceId, IEnumerable<Notice> notices, CancellationToken ct)
    {
        foreach (var n in notices)
        {
            try
            {
                await dispatcher.NotifyAsync(n.Event, workspaceId, n.LinkSetId, n.LinkId, n.Text, ct);
            }
            catch (Exception)
            {
                // Telling people is best effort: the post's result is already saved.
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The start of a post, on one line, to recognise it by.</summary>
    public static string Excerpt(string? content, int max = 60)
    {
        var one = Spaces().Replace(content ?? "", " ").Trim();
        if (one.Length <= max) return one;
        var cut = char.IsHighSurrogate(one[max - 1]) ? max - 1 : max;
        return one[..cut].TrimEnd() + "…";
    }

    /// <summary>The group a post went to: its name and, when it has one, its code.</summary>
    public static string Group(Post p) => string.IsNullOrWhiteSpace(p.Code) ? p.Target : $"{p.Target} ({p.Code})";

    public static Notice Posted(Post p, Guid? linkSetId, bool awaitingApproval) => new(
        NotifyEvent.Success, linkSetId, p.LinkId,
        $"{(awaitingApproval ? "ส่งโพสต์แล้ว รอแอดมินกลุ่มอนุมัติ" : "โพสต์สำเร็จ")} · {Group(p)} · “{Excerpt(p.Content)}”");

    public static Notice Failed(Post p, Guid? linkSetId, string? error) => new(
        NotifyEvent.Fail, linkSetId, p.LinkId,
        $"โพสต์ล้มเหลว · {Group(p)} · {(string.IsNullOrWhiteSpace(error) ? "ไม่ทราบสาเหตุ" : Excerpt(error, 120))} · “{Excerpt(p.Content, 40)}”");

    public static Notice NeedsLogin(Post p, Guid? linkSetId, Device device) => new(
        NotifyEvent.Block, linkSetId, p.LinkId,
        $"บัญชี Facebook ของเครื่อง {device.Name} ต้องเข้าสู่ระบบใหม่ จึงโพสต์ไปที่ {Group(p)} ไม่ได้");

    public static Notice Blocked(Post p, Guid? linkSetId, Device device, string? error, TimeSpan? pause) => new(
        NotifyEvent.Block, linkSetId, p.LinkId,
        $"Facebook ขัดขวางการโพสต์ของเครื่อง {device.Name} ที่ {Group(p)}" +
        (string.IsNullOrWhiteSpace(error) ? "" : $": {Excerpt(error, 120)}") +
        (pause is { } d ? $" · ระบบพักเครื่องนี้ประมาณ {Hours(d)}" : ""));

    public static Notice FailStreak(Device device, int streak, TimeSpan pause) => new(
        NotifyEvent.Block, null, null,
        $"โพสต์ของเครื่อง {device.Name} ล้มเหลวติดกัน {streak} ครั้ง ระบบพักเครื่องนี้ประมาณ {Hours(pause)}");

    public static Notice LinkSwitchedOff(SetLink link, int streak) => new(
        NotifyEvent.Fail, link.LinkSetId, link.Id,
        $"ปิดกลุ่ม {(link.Name.Length > 0 ? link.Name : link.Url)} อัตโนมัติ เพราะโพสต์ล้มเหลวติดกัน {streak} ครั้ง เปิดกลับได้ที่หน้าชุดลิงก์กลุ่ม");

    public static Notice QuotaReached(int failed, string detail) => new(
        NotifyEvent.Quota, null, null,
        $"ครบโควตา: {detail} · โพสต์ {failed} รายการถูกตีเป็นล้มเหลว");

    public static Notice RoundDone(Schedule schedule, string slotKey, IReadOnlyList<Post> round)
    {
        var ok = round.Count(p => p.Status == PostStatus.Success);
        var pending = round.Count(p => p.Status == PostStatus.Pending);
        var failed = round.Count(p => p.Status == PostStatus.Failed);
        var skipped = round.Count(p => p.Status == PostStatus.Skipped);
        var parts = new List<string> { $"สำเร็จ {ok}" };
        if (pending > 0) parts.Add($"รออนุมัติ {pending}");
        parts.Add($"ล้มเหลว {failed}");
        if (skipped > 0) parts.Add($"ข้าม {skipped}");
        var at = slotKey.Replace('T', ' ');
        return new Notice(NotifyEvent.Round, schedule.LinkSetId, null,
            $"จบรอบโพสต์ {at} · ตาราง “{schedule.Name}” · {string.Join(" · ", parts)} จากทั้งหมด {round.Count}");
    }

    private static string Hours(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{Math.Round(d.TotalHours, MidpointRounding.AwayFromZero):0} ชั่วโมง" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes)):0} นาที";

    /// <summary>
    /// The rounds (schedule + slot) among <paramref name="slots"/> that are finished now: nothing in them is queued,
    /// waiting or being posted. One message each, summing the round up.
    /// </summary>
    public static async Task<IReadOnlyList<Notice>> FinishedRoundsAsync(
        IPostRepository posts, IScheduleRepository schedules, Guid workspaceId, IEnumerable<(Guid ScheduleId, string SlotKey)> slots,
        CancellationToken ct)
    {
        var notices = new List<Notice>();
        foreach (var (scheduleId, slotKey) in slots.Distinct())
        {
            if (await posts.CountOpenInSlotAsync(scheduleId, slotKey, ct) > 0) continue;
            var schedule = await schedules.GetAsync(workspaceId, scheduleId, ct);
            if (schedule is null) continue;
            var round = await posts.ListBySlotAsync(scheduleId, slotKey, ct);
            if (round.Count > 0) notices.Add(RoundDone(schedule, slotKey, round));
        }
        return notices;
    }
}
