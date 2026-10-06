using System.Globalization;
using System.Text.RegularExpressions;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using static SIRIAUTOPOST.Application.Common.NotificationText;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>One message for the notification system (Telegram HTML, see <see cref="NotificationText"/>), with the picture of the posting window when the device sent one.</summary>
public sealed record Notice(NotifyEvent Event, Guid? LinkSetId, Guid? LinkId, string Text, byte[]? Photo = null);

/// <summary>
/// Where a post sits, for the line under a group's name ("ชุด … · ตาราง … · รอบ 2 (กลุ่ม 3/69)") and the time of what
/// comes next. A post without a schedule (a test post) only knows its set.
/// </summary>
/// <param name="Round">The round's number among the schedule's rounds of that day; null when unknown.</param>
/// <param name="Position">The post's place in its round (1-based); 0 when it has none.</param>
/// <param name="NextAt">The next post of the round, or when the next round starts once this one is done; null when nothing is queued.</param>
/// <param name="RoundEnded">No post of the round is left to go, so <paramref name="NextAt"/> is about the next round.</param>
public sealed record PostContext(
    string? SetName, string? ScheduleName, int? Round, int Position, int Total, DateTimeOffset? NextAt, bool RoundEnded, int UtcOffsetMinutes)
{
    public static readonly PostContext None = new(null, null, null, 0, 0, null, false, DefaultUtcOffsetMinutes);

    /// <summary>"ชุด SIRI · ตารางเช้า · รอบ 1 (กลุ่ม 3/69)": the parts that are known; null when none is.</summary>
    public string? WhereLine()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(SetName)) parts.Add($"ชุด {Esc(SetName)}");
        if (!string.IsNullOrWhiteSpace(ScheduleName)) parts.Add(Esc(ScheduleName));
        var round = Round is { } n ? $"รอบ {n}" : "";
        var place = Total > 0 && Position > 0 ? $"(กลุ่ม {Position}/{Total})" : "";
        var tail = string.Join(' ', new[] { round, place }.Where(s => s.Length > 0));
        if (tail.Length > 0) parts.Add(tail);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"⏭ กลุ่มถัดไป: 03/10 16:41:35" (or the next round); null when nothing is queued.</summary>
    public string? NextLine() =>
        NextAt is { } at ? $"⏭ {(RoundEnded ? "รอบถัดไป" : "กลุ่มถัดไป")}: {Time(at, UtcOffsetMinutes)}" : null;
}

/// <summary>
/// What the posting engine tells people (Telegram, LINE) and the Thai wording of it. Messages are collected while a
/// claim or a result is handled and sent after the changes are saved; a notification that fails never fails the call.
/// A message reads like the ones the extension has always sent to Telegram: an emoji and a bold headline, the group's
/// name and address, the reason, which set and round it was, and when the next group goes.
/// </summary>
public static partial class EngineNotices
{
    public static async Task SendAsync(INotificationDispatcher dispatcher, Guid workspaceId, IEnumerable<Notice> notices, CancellationToken ct)
    {
        foreach (var n in notices)
        {
            try
            {
                await dispatcher.NotifyAsync(n.Event, workspaceId, n.LinkSetId, n.LinkId, n.Text, ct, n.Photo);
            }
            catch (Exception)
            {
                // Telling people is best effort: the post's result is already saved.
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The start of a text, on one line.</summary>
    public static string Excerpt(string? content, int max = 60)
    {
        var one = Spaces().Replace(content ?? "", " ").Trim();
        if (one.Length <= max) return one;
        var cut = char.IsHighSurrogate(one[max - 1]) ? max - 1 : max;
        return one[..cut].TrimEnd() + "…";
    }

    /// <summary>The group a post went to: its name and, when it has one, its code.</summary>
    public static string Group(Post p) => string.IsNullOrWhiteSpace(p.Code) ? p.Target : $"{p.Target} ({p.Code})";

    /// <summary>The group's name in bold and, under it, its address.</summary>
    private static string GroupLines(Post p) =>
        string.IsNullOrWhiteSpace(p.TargetUrl) ? Bold(Group(p)) : $"{Bold(Group(p))}\n{Esc(p.TargetUrl)}";

    // A post sent from the test page says so, so nobody mistakes it for a schedule's.
    private static string Test(Post p) => p.IsTest ? "ทดสอบ · " : "";

    private static string Lines(params string?[] lines) => string.Join('\n', lines.Where(l => !string.IsNullOrEmpty(l)));

    private static string Reason(string? error, int max = 200) => string.IsNullOrWhiteSpace(error) ? "ไม่ทราบสาเหตุ" : Excerpt(error, max);

    public static Notice Posted(Post p, Guid? linkSetId, bool awaitingApproval, PostContext ctx, DateTimeOffset now) => new(
        NotifyEvent.Success, linkSetId, p.LinkId,
        Lines(
            awaitingApproval ? $"⏳ {Bold(Test(p) + "ส่งโพสต์แล้ว รอแอดมินกลุ่มอนุมัติ")}" : $"✅ {Bold(Test(p) + "โพสต์สำเร็จ")}",
            GroupLines(p),
            ctx.WhereLine(),
            $"🕒 {Time(now, ctx.UtcOffsetMinutes)}",
            ctx.NextLine()));

    public static Notice Failed(Post p, Guid? linkSetId, string? error, PostContext ctx) => new(
        NotifyEvent.Fail, linkSetId, p.LinkId,
        Lines(
            $"❌ {Bold(Test(p) + "โพสต์ไม่สำเร็จ ข้ามกลุ่มนี้")}",
            GroupLines(p),
            $"สาเหตุ: {Esc(Reason(error))}",
            ctx.WhereLine(),
            ctx.NextLine()));

    public static Notice NeedsLogin(Post p, Guid? linkSetId, Device device, PostContext ctx) => new(
        NotifyEvent.Block, linkSetId, p.LinkId,
        Lines(
            $"⚠️ {Bold(Test(p) + "บัญชี Facebook ต้องเข้าสู่ระบบใหม่")}",
            $"เครื่อง {Esc(device.Name)} จึงโพสต์ไปที่กลุ่มนี้ไม่ได้",
            GroupLines(p),
            ctx.WhereLine()));

    public static Notice Blocked(Post p, Guid? linkSetId, Device device, string? error, TimeSpan? pause, PostContext ctx) => new(
        NotifyEvent.Block, linkSetId, p.LinkId,
        Lines(
            $"⚠️ {Bold(Test(p) + "Facebook ขัดขวางการโพสต์")}",
            $"เครื่อง {Esc(device.Name)}",
            GroupLines(p),
            string.IsNullOrWhiteSpace(error) ? null : $"สาเหตุ: {Esc(Excerpt(error, 200))}",
            ctx.WhereLine(),
            pause is { } d ? $"⏸ ระบบพักเครื่องนี้ประมาณ {Hours(d)}" : null));

    public static Notice FailStreak(Device device, int streak, TimeSpan pause) => new(
        NotifyEvent.Block, null, null,
        Lines(
            $"⏸ {Bold($"โพสต์ล้มเหลวติดกัน {streak} ครั้ง")}",
            $"เครื่อง {Esc(device.Name)}",
            $"ระบบพักเครื่องนี้ประมาณ {Hours(pause)}"));

    public static Notice LinkSwitchedOff(SetLink link, int streak) => new(
        NotifyEvent.Fail, link.LinkSetId, link.Id,
        Lines(
            $"🚫 {Bold("ปิดกลุ่มอัตโนมัติ")}",
            link.Name.Length > 0 ? $"{Bold(link.Name)}\n{Esc(link.Url)}" : Bold(link.Url),
            $"สาเหตุ: โพสต์ล้มเหลวติดกัน {streak} ครั้ง",
            "เปิดกลับได้ที่หน้าชุดลิงก์กลุ่ม"));

    public static Notice QuotaReached(int failed, string detail) => new(
        NotifyEvent.Quota, null, null,
        Lines(
            $"📛 {Bold("ครบโควตาการโพสต์")}",
            Esc(detail),
            $"โพสต์ {failed} รายการถูกตีเป็นล้มเหลว"));

    // ---- jobs and the machines that take them ----

    /// <summary>A device took a post: which machine is posting to which group now (the <see cref="NotifyEvent.Job"/> event).</summary>
    public static Notice JobTaken(Post p, Guid? linkSetId, Device device, PostContext ctx, DateTimeOffset now) => new(
        NotifyEvent.Job, linkSetId, p.LinkId,
        Lines(
            $"🚀 {Bold($"{Test(p)}เครื่อง {device.Name} เริ่มโพสต์")}",
            GroupLines(p),
            ctx.WhereLine(),
            $"🕒 {Time(now, ctx.UtcOffsetMinutes)}"));

    /// <summary>A device took a bump: it comments on a post it made earlier.</summary>
    public static Notice BumpTaken(PostBump b, Device device, string? scheduleName, DateTimeOffset now) => new(
        NotifyEvent.Job, null, null,
        Lines(
            $"💬 {Bold($"เครื่อง {device.Name} เริ่มดันโพสต์")}",
            Bold(b.Target),
            string.IsNullOrWhiteSpace(b.Url) ? null : Esc(b.Url),
            string.IsNullOrWhiteSpace(scheduleName) ? null : $"ตาราง “{Esc(scheduleName)}”",
            $"🕒 {Time(now, NotificationText.DefaultUtcOffsetMinutes)}"));

    /// <summary>The device's report on a bump: the comment is there, or why not.</summary>
    public static Notice BumpDone(PostBump b, Device device, bool ok, bool needsLogin, bool blocked, string? error, string? scheduleName) => new(
        ok ? NotifyEvent.Job : needsLogin || blocked ? NotifyEvent.Block : NotifyEvent.Fail, null, null,
        Lines(
            ok ? $"✅ {Bold("ดันโพสต์แล้ว")}"
                : needsLogin ? $"⚠️ {Bold("ดันโพสต์ไม่ได้: บัญชี Facebook ต้องเข้าสู่ระบบใหม่")}"
                : blocked ? $"⚠️ {Bold("Facebook ขัดขวางการดันโพสต์")}"
                : $"❌ {Bold("ดันโพสต์ไม่สำเร็จ")}",
            $"เครื่อง {Esc(device.Name)}",
            Bold(b.Target),
            string.IsNullOrWhiteSpace(b.Url) ? null : Esc(b.Url),
            ok ? null : $"สาเหตุ: {Esc(Reason(error))}",
            string.IsNullOrWhiteSpace(scheduleName) ? null : $"ตาราง “{Esc(scheduleName)}”"));

    /// <summary>A post a device took was never reported on: it is failed and not retried (it may have gone out).</summary>
    public static Notice JobLost(Post p, Guid? linkSetId, Device device, PostContext ctx) => new(
        NotifyEvent.Fail, linkSetId, p.LinkId,
        Lines(
            $"⌛ {Bold($"{Test(p)}เครื่อง {device.Name} ไม่ส่งผลการโพสต์กลับมา")}",
            GroupLines(p),
            "สาเหตุ: ไม่ได้รับผลภายใน 15 นาที งานนี้ถูกตีเป็นล้มเหลวและจะไม่โพสต์ซ้ำ (อาจโพสต์ไปแล้ว ควรตรวจในกลุ่ม)",
            ctx.WhereLine()));

    /// <summary>Posts that waited so long (the machine was off) that they were skipped instead of going out late.</summary>
    public static Notice LateSkipped(int count, Device device, string detail) => new(
        NotifyEvent.Offline, null, null,
        Lines(
            $"⏭ {Bold($"ข้ามโพสต์ที่ช้าเกินไป {count} รายการ")}",
            $"เครื่อง {Esc(device.Name)}",
            Esc(detail)));

    public static Notice DeviceJobsPaused(Device device, bool paused, int waiting) => new(
        NotifyEvent.StartStop, null, null,
        paused
            ? Lines(
                $"⏸ {Bold($"หยุดรับงานเครื่อง {device.Name}")}",
                "สั่งจากเว็บ: เครื่องนี้จะไม่รับงานโพสต์ใหม่จนกว่าจะเปิดอีกครั้ง",
                waiting > 0 ? $"งานที่รออยู่ {waiting} รายการ" : null)
            : Lines(
                $"▶️ {Bold($"เปิดรับงานเครื่อง {device.Name}")}",
                "สั่งจากเว็บ: เครื่องนี้กลับมารับงานโพสต์ต่อ",
                waiting > 0 ? $"งานที่รออยู่ {waiting} รายการ" : null));

    /// <summary>The engine's own rest (after a Facebook block or posts that kept failing) is over.</summary>
    public static Notice DevicePauseEnded(Device device, string? reason) => new(
        NotifyEvent.StartStop, null, null,
        Lines(
            $"▶️ {Bold($"เครื่อง {device.Name} พ้นช่วงพักแล้ว")}",
            string.IsNullOrWhiteSpace(reason) ? null : $"ที่พักเพราะ: {Esc(reason)}",
            "กลับมารับงานต่อ"));

    public static Notice DevicePaired(Device device) => new(
        NotifyEvent.StartStop, null, null,
        Lines(
            $"🔗 {Bold($"เครื่องใหม่เชื่อมต่อแล้ว: {device.Name}")}",
            string.IsNullOrWhiteSpace(device.Browser) ? null : Esc(device.Browser)));

    public static Notice DeviceUnbound(Device device, int cancelled) => new(
        NotifyEvent.StartStop, null, null,
        Lines(
            $"🔌 {Bold($"ยกเลิกการผูกเครื่อง {device.Name}")}",
            cancelled > 0 ? $"งานที่ค้างอยู่ {cancelled} รายการถูกยกเลิก" : null));

    /// <summary>A device is silent while posts are due for it: they wait until it is back.</summary>
    public static Notice DeviceStalled(Device device, TimeSpan silent, int due, DateTimeOffset oldest, int utcOffsetMinutes) => new(
        NotifyEvent.Offline, null, null,
        Lines(
            $"📴 {Bold($"เครื่อง {device.Name} ออฟไลน์ งานค้างรอ")}",
            device.LastSeenAt is { } seen
                ? $"ไม่มีสัญญาณมา {Hours(silent)} (ล่าสุด {Time(seen, utcOffsetMinutes)})"
                : $"ไม่มีสัญญาณมา {Hours(silent)}",
            $"โพสต์ที่ถึงเวลาแล้วแต่ยังไม่ได้โพสต์ {due} รายการ",
            $"เก่าสุดครบกำหนด {Time(oldest, utcOffsetMinutes)}"));

    public static Notice DeviceBack(Device device, TimeSpan gone) => new(
        NotifyEvent.Offline, null, null,
        Lines(
            $"📶 {Bold($"เครื่อง {device.Name} กลับมาออนไลน์")}",
            $"หายไปประมาณ {Hours(gone)} เริ่มรับงานที่ค้างต่อ"));

    // ---- schedules ----

    public static Notice SchedulePaused(Schedule s, int removed) => new(
        NotifyEvent.StartStop, s.LinkSetId, null,
        Lines(
            $"⏸ {Bold($"หยุดตาราง “{s.Name}”")}",
            removed > 0 ? $"ลบคิวที่ยังไม่ได้โพสต์ {removed} รายการ" : "ไม่มีคิวค้าง",
            "เปิดตารางอีกครั้งเพื่อสร้างคิวใหม่"));

    public static Notice ScheduleResumed(Schedule s) => new(
        NotifyEvent.StartStop, s.LinkSetId, null,
        Lines(
            $"▶️ {Bold($"เปิดตาราง “{s.Name}” ต่อ")}",
            "ระบบสร้างคิวโพสต์ใหม่ตามเวลาที่ตั้งไว้"));

    public static Notice ScheduleDeleted(Schedule s, int removed) => new(
        NotifyEvent.StartStop, s.LinkSetId, null,
        Lines(
            $"🗑 {Bold($"ลบตาราง “{s.Name}”")}",
            removed > 0 ? $"ลบคิวที่ยังไม่ได้โพสต์ {removed} รายการ" : null));

    /// <summary>A once-only schedule has run its day and switched itself off.</summary>
    public static Notice ScheduleFinished(Schedule s) => new(
        NotifyEvent.StartStop, s.LinkSetId, null,
        Lines(
            $"🏁 {Bold($"ตาราง “{s.Name}” ทำงานครบแล้ว")}",
            "ตารางแบบครั้งเดียวปิดตัวเองเพราะพ้นวันที่กำหนดแล้ว"));

    /// <summary>At most this many failed groups are named in a round's summary.</summary>
    private const int MaxFailedListed = 10;

    public static Notice RoundDone(Schedule schedule, string slotKey, IReadOnlyList<Post> round, DateTimeOffset? nextRoundAt)
    {
        var ok = round.Count(p => p.Status == PostStatus.Success);
        var pending = round.Count(p => p.Status == PostStatus.Pending);
        var failed = round.Where(p => p.Status == PostStatus.Failed).ToList();
        var skipped = round.Count(p => p.Status == PostStatus.Skipped);
        var lines = new List<string?>
        {
            $"📊 {Bold($"สรุปรอบ {RoundLabel(slotKey)}")} · ตาราง “{Esc(schedule.Name)}”",
            $"ทั้งหมด {round.Count} กลุ่ม",
            $"✅ สำเร็จ {ok} กลุ่ม",
        };
        if (pending > 0) lines.Add($"⏳ รออนุมัติ {pending} กลุ่ม");
        if (failed.Count > 0)
        {
            lines.Add($"❌ ไม่สำเร็จ {failed.Count} กลุ่ม");
            lines.AddRange(failed.Take(MaxFailedListed).Select(p => $"• {Esc(Group(p))} — {Esc(Reason(p.FailureDetail, 80))}"));
            if (failed.Count > MaxFailedListed) lines.Add($"• และอีก {failed.Count - MaxFailedListed} กลุ่ม");
        }
        if (skipped > 0) lines.Add($"⏭ ข้าม {skipped} กลุ่ม");
        if (nextRoundAt is { } next) lines.Add($"🔄 รอบถัดไป: {Time(next, schedule.UtcOffsetMinutes)}");
        return new Notice(NotifyEvent.Round, schedule.LinkSetId, null, Lines(lines.ToArray()));
    }

    /// <summary>"06/10 10:00" for the round of that day and time, "06/10 เริ่มทันที" for the opening round of a schedule.</summary>
    private static string RoundLabel(string slotKey)
    {
        if (slotKey.Length < 10 || !DateOnly.TryParseExact(slotKey[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return slotKey;
        var rest = slotKey.Length > 11 ? slotKey[11..] : "";
        return $"{day.ToString("dd/MM", CultureInfo.InvariantCulture)} {(rest == Schedule.NowSlot ? "เริ่มทันที" : rest)}".TrimEnd();
    }

    internal static string Hours(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{Math.Round(d.TotalHours, MidpointRounding.AwayFromZero):0} ชั่วโมง" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes)):0} นาที";

    /// <summary>
    /// Where <paramref name="post"/> stands in its round: the set's name, the schedule's, the round's number among that
    /// day's rounds, its place in the round and when the next post goes. Three small queries; a post without a schedule
    /// (a test post) only gets its set's name. Call it before the post's own result is saved: it leaves the post itself
    /// out of "what is still to go".
    /// </summary>
    public static async Task<PostContext> ContextAsync(
        IPostRepository posts, IScheduleRepository schedules, ILinkSetRepository linkSets,
        Guid workspaceId, Post post, Guid? linkSetId, DateTimeOffset now, CancellationToken ct)
    {
        var setName = linkSetId is { } setId ? (await linkSets.GetAsync(workspaceId, setId, ct))?.Name : null;
        if (post.ScheduleId is not { } scheduleId || post.SlotKey is not { } slotKey)
            return PostContext.None with { SetName = setName };

        var schedule = await schedules.GetAsync(workspaceId, scheduleId, ct);
        var offset = schedule?.UtcOffsetMinutes ?? DefaultUtcOffsetMinutes;
        var slot = await posts.ListBySlotAsync(scheduleId, slotKey, ct);

        var position = 0;
        for (var i = 0; i < slot.Count && position == 0; i++)
            if (slot[i].Id == post.Id) position = i + 1;
        var nextInRound = slot.Where(p => p.Id != post.Id && p.Status == PostStatus.Queued).Select(p => (DateTimeOffset?)p.ScheduledAt).Min();
        var next = nextInRound
            ?? ((await posts.NextQueuedAtByScheduleAsync([scheduleId], now, ct)).TryGetValue(scheduleId, out var at) ? at : (DateTimeOffset?)null);

        int? round = null;
        if (slotKey.Length >= 10)
        {
            var keys = (await posts.ListSlotKeysOfDayAsync(scheduleId, slotKey[..10], ct))
                .OrderBy(k => k.EndsWith("T" + Schedule.NowSlot, StringComparison.Ordinal) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();
            var index = keys.IndexOf(slotKey);
            if (index >= 0) round = index + 1;
        }
        return new PostContext(setName, schedule?.Name, round, position, slot.Count, next, nextInRound is null, offset);
    }

    /// <summary>
    /// The rounds (schedule + slot) among <paramref name="slots"/> that are finished now: nothing in them is queued,
    /// waiting or being posted. One message each, summing the round up.
    /// </summary>
    public static async Task<IReadOnlyList<Notice>> FinishedRoundsAsync(
        IPostRepository posts, IScheduleRepository schedules, Guid workspaceId, IEnumerable<(Guid ScheduleId, string SlotKey)> slots,
        DateTimeOffset now, CancellationToken ct)
    {
        var notices = new List<Notice>();
        foreach (var (scheduleId, slotKey) in slots.Distinct())
        {
            if (await posts.CountOpenInSlotAsync(scheduleId, slotKey, ct) > 0) continue;
            var schedule = await schedules.GetAsync(workspaceId, scheduleId, ct);
            if (schedule is null) continue;
            var round = await posts.ListBySlotAsync(scheduleId, slotKey, ct);
            if (round.Count == 0) continue;
            var next = (await posts.NextQueuedAtByScheduleAsync([scheduleId], now, ct)).TryGetValue(scheduleId, out var at) ? at : (DateTimeOffset?)null;
            notices.Add(RoundDone(schedule, slotKey, round, next));
        }
        return notices;
    }
}
