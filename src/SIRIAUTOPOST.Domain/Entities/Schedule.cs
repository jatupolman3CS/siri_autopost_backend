using System.Globalization;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Entities;

// "ตารางโพสต์": pairs one collection with one link set and says when to post. The schedule never posts by itself: it
// materialises ordinary queued Posts a fortnight ahead (in its own local calendar, UtcOffsetMinutes), which the
// paired browser claims through the device job API.
public class Schedule : Entity
{
    public const int MaxNameLength = 120;
    /// <summary>Schedules per workspace.</summary>
    public const int MaxPerWorkspace = 50;
    /// <summary>Posts are queued this many local days ahead (today and the 13 days after).</summary>
    public const int HorizonDays = 14;
    /// <summary>One materialising run refuses to create more posts than this.</summary>
    public const int MaxPostsPerRun = 2000;
    /// <summary>
    /// A workspace holds at most this many queued posts that are still in the future (every source counted). A run that
    /// would go beyond is refused when someone asks for it, and left for later by the background top-up.
    /// </summary>
    public const int MaxQueuedPerWorkspace = 10_000;
    /// <summary>A start date may be this many days before the schedule's today (a browser a day behind)...</summary>
    public const int StartDateDaysBack = 1;
    /// <summary>...and this many days after it (a year ahead, leap year included).</summary>
    public const int StartDateDaysAhead = 366;
    public const int MaxTimes = 48;
    public static readonly int[] BumpOptions = [0, 6, 12, 24];
    public static readonly int[] AutoDeleteOptions = [0, 3, 7, 14];

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    public Guid CollectionId { get; private set; }
    public Guid LinkSetId { get; private set; }
    public ScheduleMode Mode { get; private set; }
    /// <summary>Daily, weekdays and weekend post at these times ("HH:mm", distinct, sorted).</summary>
    public List<string> Times { get; private set; } = [];
    /// <summary>Interval: a round every N hours (1-24)...</summary>
    public int EveryHours { get; private set; } = 6;
    /// <summary>...starting at this time.</summary>
    public string FirstTime { get; private set; } = "09:00";
    /// <summary>The first local day, or the only one for Once.</summary>
    public DateOnly StartDate { get; private set; }
    public string OnceTime { get; private set; } = "14:00";
    public PostOrder Order { get; private set; }
    /// <summary>Drip: DripCount posts (1-12) spread from DripFrom to DripTo.</summary>
    public string DripFrom { get; private set; } = "09:00";
    public string DripTo { get; private set; } = "21:00";
    public int DripCount { get; private set; } = 3;
    /// <summary>Stored only: the extension cannot bump posts yet (0, 6, 12 or 24 hours).</summary>
    public int BumpHours { get; private set; }
    /// <summary>Stored only: the extension cannot delete posts yet (0, 3, 7 or 14 days).</summary>
    public int AutoDeleteDays { get; private set; }
    /// <summary>
    /// Own times of a target: the key is a link's id ("N" format) or "account:&lt;id&gt;"; the value is distinct sorted
    /// "HH:mm". A target without an entry follows the schedule.
    /// </summary>
    public Dictionary<string, List<string>> Overrides { get; private set; } = new();
    public bool Active { get; private set; } = true;
    /// <summary>The browser's UTC offset in minutes (-840 to 840): the schedule's calendar is the local one.</summary>
    public int UtcOffsetMinutes { get; private set; }
    /// <summary>The last local day posts were generated for.</summary>
    public DateOnly? GeneratedThrough { get; private set; }
    /// <summary>Rotate: the position in the collection's posts, one step per slot.</summary>
    public int Cursor { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Schedule() { } // EF Core

    public static string LinkKey(Guid linkId) => linkId.ToString("N");

    public static string AccountKey(Guid accountId) => "account:" + accountId.ToString("N");

    public static Schedule Create(
        Guid workspaceId, string name, Guid collectionId, Guid linkSetId, ScheduleMode mode, IEnumerable<string>? times,
        int everyHours, string? firstTime, DateOnly startDate, string? onceTime, PostOrder order, string? dripFrom, string? dripTo,
        int dripCount, int bumpHours, int autoDeleteDays, IReadOnlyDictionary<string, IReadOnlyList<string>>? overrides,
        int utcOffsetMinutes, DateTimeOffset now)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new DomainException("กรุณาใส่ชื่อตาราง");
        if (n.Length > MaxNameLength) throw new DomainException($"ชื่อตารางยาวเกิน {MaxNameLength} ตัวอักษร");
        if (!Enum.IsDefined(mode)) throw new DomainException("รูปแบบตารางไม่ถูกต้อง");
        if (!Enum.IsDefined(order)) throw new DomainException("ลำดับโพสต์ไม่ถูกต้อง");
        if (utcOffsetMinutes is < -840 or > 840) throw new DomainException("เขตเวลาไม่ถูกต้อง");
        EnsureStartDate(startDate, now, utcOffsetMinutes);
        if (!BumpOptions.Contains(bumpHours)) throw new DomainException("ตัวเลือกดันโพสต์ไม่ถูกต้อง");
        if (!AutoDeleteOptions.Contains(autoDeleteDays)) throw new DomainException("ตัวเลือกลบโพสต์อัตโนมัติไม่ถูกต้อง");

        // Fields that belong to another mode are not checked: they fall back to their defaults when they do not make sense.
        var interval = mode == ScheduleMode.Interval;
        var drip = mode == ScheduleMode.Drip;
        if (interval && everyHours is < 1 or > 24) throw new DomainException("ความถี่ต้องอยู่ระหว่าง 1–24 ชั่วโมง");
        if (drip && dripCount is < 1 or > 12) throw new DomainException("จำนวนโพสต์ต่อวันต้องอยู่ระหว่าง 1–12");
        if (!interval && everyHours is < 1 or > 24) everyHours = 6;
        if (!drip && dripCount is < 1 or > 12) dripCount = 3;

        var cleanTimes = CleanTimes(times ?? []);
        if (cleanTimes.Count > MaxTimes) throw new DomainException($"เลือกเวลาได้ไม่เกิน {MaxTimes} เวลา");
        if (mode is ScheduleMode.Daily or ScheduleMode.Weekdays or ScheduleMode.Weekend && cleanTimes.Count == 0)
            throw new DomainException("เลือกเวลาโพสต์อย่างน้อย 1 เวลา");

        var first = Time(firstTime, "09:00", "เวลาเริ่มต้นไม่ถูกต้อง", interval);
        var once = Time(onceTime, "14:00", "เวลาโพสต์ไม่ถูกต้อง", mode == ScheduleMode.Once);
        var from = Time(dripFrom, "09:00", "เวลาเริ่มช่วงไม่ถูกต้อง", drip);
        var to = Time(dripTo, "21:00", "เวลาสิ้นสุดช่วงไม่ถูกต้อง", drip);
        if (drip && TimeOfDay.Parse(from)! > TimeOfDay.Parse(to)!)
            throw new DomainException("เวลาเริ่มช่วงต้องไม่เกินเวลาสิ้นสุด");

        return new Schedule
        {
            WorkspaceId = workspaceId,
            Name = n,
            CollectionId = collectionId,
            LinkSetId = linkSetId,
            Mode = mode,
            Times = cleanTimes,
            EveryHours = everyHours,
            FirstTime = first,
            StartDate = startDate,
            OnceTime = once,
            Order = order,
            DripFrom = from,
            DripTo = to,
            DripCount = dripCount,
            BumpHours = bumpHours,
            AutoDeleteDays = autoDeleteDays,
            Overrides = CleanOverrides(overrides),
            UtcOffsetMinutes = utcOffsetMinutes,
            CreatedAt = now,
        };
    }

    /// <summary>"Today" in a calendar that is <paramref name="utcOffsetMinutes"/> ahead of UTC.</summary>
    public static DateOnly LocalDayOf(DateTimeOffset now, int utcOffsetMinutes) =>
        DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -840, 840))).DateTime);

    /// <summary>The first and last start date a new schedule may have: from yesterday to 366 days ahead, in its own calendar.</summary>
    public static (DateOnly Min, DateOnly Max) StartDateRange(DateTimeOffset now, int utcOffsetMinutes)
    {
        var today = LocalDayOf(now, utcOffsetMinutes);
        return (today.AddDays(-StartDateDaysBack), today.AddDays(StartDateDaysAhead));
    }

    /// <summary>
    /// A start date far outside the range is no use (and one near <c>9999-12-31</c> breaks every calculation that follows
    /// it), so it is refused.
    /// </summary>
    public static void EnsureStartDate(DateOnly startDate, DateTimeOffset now, int utcOffsetMinutes)
    {
        var (min, max) = StartDateRange(now, utcOffsetMinutes);
        if (startDate < min || startDate > max) throw new DomainException(StartDateMessage(min, max));
    }

    public static string StartDateMessage(DateOnly min, DateOnly max) =>
        $"วันที่เริ่มต้องอยู่ระหว่าง {min.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ถึง {max.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} " +
        $"(ตั้งแต่เมื่อวานถึง {StartDateDaysAhead} วันข้างหน้า)";

    /// <summary>The posting times of one day, ascending ("HH:mm").</summary>
    public IReadOnlyList<string> Slots()
    {
        switch (Mode)
        {
            case ScheduleMode.Once:
                return [OnceTime];
            case ScheduleMode.Interval:
            {
                var start = TimeOfDay.Parse(FirstTime) ?? 9 * 60;
                var step = Math.Max(1, EveryHours) * 60;
                var list = new List<string>();
                for (var t = start; t < 24 * 60; t += step) list.Add(TimeOfDay.Format(t));
                return list;
            }
            case ScheduleMode.Drip:
            {
                var a = TimeOfDay.Parse(DripFrom) ?? 9 * 60;
                var b = TimeOfDay.Parse(DripTo) ?? 21 * 60;
                var n = Math.Clamp(DripCount, 1, 12);
                var span = Math.Max(0, b - a);
                var list = new List<string>();
                for (var i = 0; i < n; i++)
                {
                    var offset = n == 1 ? span / 2.0 : (double)span * i / (n - 1);
                    var t = a + (int)Math.Round(offset, MidpointRounding.AwayFromZero);
                    var s = TimeOfDay.Format(Math.Min(t, 24 * 60 - 1));
                    if (!list.Contains(s)) list.Add(s);
                }
                return list;
            }
            default:
                return Times;
        }
    }

    /// <summary>The slots of one target: its own times when it has them, else the schedule's.</summary>
    public IReadOnlyList<string> SlotsFor(string targetKey) =>
        Overrides.TryGetValue(targetKey, out var own) && own.Count > 0 ? own : Slots();

    /// <summary>Does the schedule post on this local day? Weekend is Friday to Sunday.</summary>
    public bool Matches(DateOnly localDay)
    {
        if (Mode == ScheduleMode.Once) return localDay == StartDate;
        if (localDay < StartDate) return false;
        return Mode switch
        {
            ScheduleMode.Weekdays => localDay.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            ScheduleMode.Weekend => localDay.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true,
        };
    }

    /// <summary>"Today" in the schedule's calendar.</summary>
    public DateOnly LocalDay(DateTimeOffset now) => LocalDayOf(now, UtcOffsetMinutes);

    /// <summary>A local day and time of day ("HH:mm") as a UTC instant.</summary>
    public DateTimeOffset ToUtc(DateOnly localDay, string hhmm)
    {
        var minutes = TimeOfDay.Parse(hhmm) ?? throw new DomainException("เวลาไม่ถูกต้อง");
        var local = localDay.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromMinutes(UtcOffsetMinutes)).ToUniversalTime();
    }

    /// <summary>The slot's key on a local day: "yyyy-MM-ddTHH:mm" (what Post.SlotKey stores).</summary>
    public static string SlotKey(DateOnly localDay, string hhmm) => $"{localDay:yyyy-MM-dd}T{hhmm}";

    /// <summary>Resumes or pauses. Resuming forgets what was generated, so the next run fills the horizon again.</summary>
    public void SetActive(bool active)
    {
        if (active == Active) return;
        Active = active;
        if (active) GeneratedThrough = null;
    }

    /// <summary>Remembers how far posts were generated and where Rotate stands.</summary>
    public void MarkGenerated(DateOnly through, int cursor)
    {
        GeneratedThrough = through;
        Cursor = Math.Max(0, cursor);
    }

    /// <summary>The next materialising run starts from today again (for a restored schedule).</summary>
    public void ResetGenerated()
    {
        GeneratedThrough = null;
        Cursor = 0;
    }

    private static string Time(string? value, string fallback, string error, bool relevant)
    {
        var v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (TimeOfDay.Parse(v) is { } m) return TimeOfDay.Format(m);
        return relevant ? throw new DomainException(error) : fallback;
    }

    private static List<string> CleanTimes(IEnumerable<string> times)
    {
        var set = new SortedSet<int>();
        foreach (var t in times)
        {
            var m = TimeOfDay.Parse(t) ?? throw new DomainException($"เวลา \"{t}\" ไม่ถูกต้อง ใช้รูปแบบ HH:mm");
            set.Add(m);
        }
        return set.Select(TimeOfDay.Format).ToList();
    }

    private static Dictionary<string, List<string>> CleanOverrides(IReadOnlyDictionary<string, IReadOnlyList<string>>? overrides)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var (key, list) in overrides ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var k = NormalizeKey(key);
            var times = CleanTimes(list ?? []);
            if (times.Count == 0) continue; // an empty list means "follow the schedule"
            if (times.Count > MaxTimes) throw new DomainException($"เลือกเวลาได้ไม่เกิน {MaxTimes} เวลา");
            result[k] = times;
        }
        return result;
    }

    /// <summary>A link's id as "N" digits, or "account:" and the account's id; anything else is refused.</summary>
    public static string NormalizeKey(string? key)
    {
        var k = (key ?? "").Trim();
        const string account = "account:";
        if (k.StartsWith(account, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(k[account.Length..], out var a)) return AccountKey(a);
        if (Guid.TryParse(k, out var l)) return LinkKey(l);
        throw new DomainException("ชื่อเวลาเฉพาะกลุ่มไม่ถูกต้อง");
    }
}
