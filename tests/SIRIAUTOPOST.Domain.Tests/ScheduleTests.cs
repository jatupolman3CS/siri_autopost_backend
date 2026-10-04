using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class ScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly DateOnly Mon = new(2026, 10, 5); // 2026-10-05 is a Monday

    private static Schedule Make(
        ScheduleMode mode = ScheduleMode.Daily, string[]? times = null, int every = 6, string first = "09:00", DateOnly? start = null,
        string once = "14:00", string dripFrom = "09:00", string dripTo = "21:00", int dripCount = 3, int offset = 420,
        Dictionary<string, IReadOnlyList<string>>? overrides = null, PostOrder order = PostOrder.Shuffle) =>
        Schedule.Create(Ws, "ตาราง", Guid.NewGuid(), Guid.NewGuid(), mode, times ?? ["09:00", "18:00"], every, first, start ?? Mon, once, order,
            dripFrom, dripTo, dripCount, 0, 0, overrides, offset, Now);

    [Fact]
    public void The_test_calendar_is_what_we_think_it_is() => Assert.Equal(DayOfWeek.Monday, Mon.DayOfWeek);

    // ---- slots ----

    [Fact]
    public void Daily_weekday_and_weekend_schedules_post_at_their_times_sorted()
    {
        foreach (var mode in new[] { ScheduleMode.Daily, ScheduleMode.Weekdays, ScheduleMode.Weekend })
            Assert.Equal(["06:30", "09:00", "21:00"], Make(mode, times: ["21:00", "06:30", "09:00", "09:00"]).Slots());
    }

    [Fact]
    public void Once_posts_at_its_time_only()
    {
        Assert.Equal(["14:30"], Make(ScheduleMode.Once, once: "14:30", times: []).Slots());
    }

    [Theory]
    [InlineData(6, "09:00", new[] { "09:00", "15:00", "21:00" })]
    [InlineData(4, "08:30", new[] { "08:30", "12:30", "16:30", "20:30" })]
    [InlineData(24, "09:00", new[] { "09:00" })]
    [InlineData(1, "22:00", new[] { "22:00", "23:00" })]
    [InlineData(12, "00:00", new[] { "00:00", "12:00" })]
    public void An_interval_schedule_runs_rounds_from_the_first_time_until_midnight(int every, string first, string[] expected) =>
        Assert.Equal(expected, Make(ScheduleMode.Interval, every: every, first: first, times: []).Slots());

    [Theory]
    [InlineData("09:00", "21:00", 3, new[] { "09:00", "15:00", "21:00" })]
    [InlineData("09:00", "21:00", 1, new[] { "15:00" })]
    [InlineData("09:00", "21:00", 2, new[] { "09:00", "21:00" })]
    [InlineData("08:00", "20:00", 4, new[] { "08:00", "12:00", "16:00", "20:00" })]
    [InlineData("09:00", "09:00", 3, new[] { "09:00" })] // no span: the slots collapse into one
    [InlineData("10:00", "10:10", 5, new[] { "10:00", "10:03", "10:05", "10:08", "10:10" })] // 2.5 rounds up, like the design
    public void A_drip_schedule_spreads_its_posts_between_two_times(string from, string to, int count, string[] expected) =>
        Assert.Equal(expected, Make(ScheduleMode.Drip, dripFrom: from, dripTo: to, dripCount: count, times: []).Slots());

    [Fact]
    public void A_drip_of_twelve_posts_gives_twelve_distinct_slots()
    {
        var slots = Make(ScheduleMode.Drip, dripFrom: "06:00", dripTo: "22:00", dripCount: 12, times: []).Slots();
        Assert.Equal(12, slots.Count);
        Assert.Equal("06:00", slots[0]);
        Assert.Equal("22:00", slots[^1]);
        Assert.Equal(slots.OrderBy(s => s), slots);
    }

    [Fact]
    public void A_target_with_its_own_times_ignores_the_schedule_s()
    {
        var link = Guid.NewGuid();
        var account = Guid.NewGuid();
        var s = Make(overrides: new()
        {
            [link.ToString("N")] = ["20:00", "07:00"],
            ["account:" + account.ToString("N").ToUpperInvariant()] = [],
        });

        Assert.Equal(["07:00", "20:00"], s.SlotsFor(Schedule.LinkKey(link)));
        Assert.Equal(s.Slots(), s.SlotsFor(Schedule.AccountKey(account))); // an empty list follows the schedule
        Assert.Equal(s.Slots(), s.SlotsFor(Schedule.LinkKey(Guid.NewGuid())));
        Assert.Single(s.Overrides);
    }

    // ---- days ----

    [Fact]
    public void Daily_matches_every_day_from_the_start_date()
    {
        var s = Make(ScheduleMode.Daily, start: Mon);
        Assert.False(s.Matches(Mon.AddDays(-1)));
        Assert.True(s.Matches(Mon));
        Assert.True(s.Matches(Mon.AddDays(400)));
    }

    [Fact]
    public void Interval_and_drip_match_every_day_from_the_start_date()
    {
        foreach (var mode in new[] { ScheduleMode.Interval, ScheduleMode.Drip })
        {
            var s = Make(mode, start: Mon);
            Assert.False(s.Matches(Mon.AddDays(-1)));
            Assert.All(Enumerable.Range(0, 14), d => Assert.True(s.Matches(Mon.AddDays(d))));
        }
    }

    [Fact]
    public void Weekdays_are_monday_to_friday()
    {
        var s = Make(ScheduleMode.Weekdays);
        var days = Enumerable.Range(0, 7).Select(d => Mon.AddDays(d)).ToList(); // Mon..Sun
        Assert.Equal([true, true, true, true, true, false, false], days.Select(s.Matches));
    }

    [Fact]
    public void Weekend_is_friday_to_sunday_as_the_design_labels_it()
    {
        var s = Make(ScheduleMode.Weekend);
        var days = Enumerable.Range(0, 7).Select(d => Mon.AddDays(d)).ToList(); // Mon..Sun
        Assert.Equal([false, false, false, false, true, true, true], days.Select(s.Matches));
        Assert.Equal(DayOfWeek.Friday, days[4].DayOfWeek);
    }

    [Fact]
    public void Weekdays_do_not_match_before_the_start_date()
    {
        var s = Make(ScheduleMode.Weekdays, start: Mon.AddDays(7));
        Assert.False(s.Matches(Mon));
        Assert.True(s.Matches(Mon.AddDays(7)));
    }

    [Fact]
    public void Once_matches_its_day_only()
    {
        var s = Make(ScheduleMode.Once, start: Mon);
        Assert.True(s.Matches(Mon));
        Assert.False(s.Matches(Mon.AddDays(-1)));
        Assert.False(s.Matches(Mon.AddDays(1)));
    }

    // ---- local calendar ----

    [Fact]
    public void Slots_are_planned_in_the_schedule_s_local_calendar()
    {
        var s = Make(offset: 420); // UTC+7
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero), s.ToUtc(Mon, "09:00"));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 18, 30, 0, TimeSpan.Zero), s.ToUtc(Mon, "01:30")); // the day before in UTC
        Assert.Equal("2026-10-05T09:00", Schedule.SlotKey(Mon, "09:00"));

        var west = Make(offset: -300); // UTC-5
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.Zero), west.ToUtc(Mon, "09:00"));
    }

    [Fact]
    public void Local_today_follows_the_offset()
    {
        var s = Make(offset: 420);
        Assert.Equal(new DateOnly(2026, 10, 4), s.LocalDay(new DateTimeOffset(2026, 10, 4, 16, 59, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 5), s.LocalDay(new DateTimeOffset(2026, 10, 4, 17, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 3), Make(offset: -300).LocalDay(new DateTimeOffset(2026, 10, 4, 4, 0, 0, TimeSpan.Zero)));
    }

    // ---- validation ----

    [Fact]
    public void Daily_weekday_and_weekend_schedules_need_a_time()
    {
        foreach (var mode in new[] { ScheduleMode.Daily, ScheduleMode.Weekdays, ScheduleMode.Weekend })
            Assert.Throws<DomainException>(() => Make(mode, times: []));
        Make(ScheduleMode.Interval, times: []);
        Make(ScheduleMode.Drip, times: []);
        Make(ScheduleMode.Once, times: []);
    }

    [Theory]
    [InlineData("9:00")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("abc")]
    [InlineData("")]
    public void Times_must_be_hh_mm(string time) => Assert.Throws<DomainException>(() => Make(times: [time]));

    [Fact]
    public void Numbers_are_kept_in_range()
    {
        Assert.Throws<DomainException>(() => Make(ScheduleMode.Interval, every: 0, times: []));
        Assert.Throws<DomainException>(() => Make(ScheduleMode.Interval, every: 25, times: []));
        Assert.Throws<DomainException>(() => Make(ScheduleMode.Drip, dripCount: 0, times: []));
        Assert.Throws<DomainException>(() => Make(ScheduleMode.Drip, dripCount: 13, times: []));
        Assert.Throws<DomainException>(() => Make(offset: 841));
        Assert.Throws<DomainException>(() => Make(offset: -841));
        Assert.Throws<DomainException>(() => Make(ScheduleMode.Drip, dripFrom: "21:00", dripTo: "09:00", times: []));
        Assert.Throws<DomainException>(() => Schedule.Create(Ws, "x", Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["09:00"], 6, "09:00", Mon, "14:00",
            PostOrder.Shuffle, "09:00", "21:00", 3, 5, 0, null, 0, Now));
        Assert.Throws<DomainException>(() => Schedule.Create(Ws, "x", Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["09:00"], 6, "09:00", Mon, "14:00",
            PostOrder.Shuffle, "09:00", "21:00", 3, 0, 2, null, 0, Now));
        Assert.Throws<DomainException>(() => Schedule.Create(Ws, " ", Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["09:00"], 6, "09:00", Mon, "14:00",
            PostOrder.Shuffle, "09:00", "21:00", 3, 0, 0, null, 0, Now));
        Assert.Throws<DomainException>(() => Schedule.Create(Ws, new string('x', 121), Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["09:00"], 6, "09:00", Mon,
            "14:00", PostOrder.Shuffle, "09:00", "21:00", 3, 0, 0, null, 0, Now));
    }

    [Fact]
    public void Fields_of_another_mode_do_not_have_to_make_sense()
    {
        // A client that only fills what its mode needs leaves the rest at 0 or blank.
        var s = Schedule.Create(Ws, "x", Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["09:00"], 0, null, Mon, null,
            PostOrder.Shuffle, null, null, 0, 0, 0, null, 0, Now);
        Assert.Equal(["09:00"], s.Slots());
        Assert.Equal(6, s.EveryHours);
        Assert.Equal(3, s.DripCount);
        Assert.Equal("09:00", s.FirstTime);
    }

    [Fact]
    public void Override_keys_must_be_a_link_id_or_an_account_key()
    {
        Assert.Throws<DomainException>(() => Make(overrides: new() { ["not-a-key"] = ["09:00"] }));
        Assert.Throws<DomainException>(() => Make(overrides: new() { [Guid.NewGuid().ToString("N")] = ["25:00"] }));
        Assert.Throws<DomainException>(() => Make(overrides: new() { ["account:xyz"] = ["09:00"] }));
    }

    [Fact]
    public void Resuming_forgets_what_was_generated_and_pausing_keeps_the_cursor()
    {
        var s = Make();
        s.MarkGenerated(new DateOnly(2026, 10, 18), 7);
        Assert.Equal(new DateOnly(2026, 10, 18), s.GeneratedThrough);
        Assert.Equal(7, s.Cursor);

        s.SetActive(false);
        Assert.False(s.Active);
        Assert.Equal(new DateOnly(2026, 10, 18), s.GeneratedThrough);

        s.SetActive(true);
        Assert.True(s.Active);
        Assert.Null(s.GeneratedThrough);
        Assert.Equal(7, s.Cursor);
    }

    // ---- the start date ----

    [Fact]
    public void A_start_date_may_be_from_yesterday_to_366_days_ahead_in_the_schedules_own_calendar()
    {
        var today = new DateOnly(2026, 10, 4); // 03:00 UTC is 10:00 on the 4th in Bangkok
        Assert.Equal((today.AddDays(-1), today.AddDays(366)), Schedule.StartDateRange(Now, 420));

        Assert.Equal(today.AddDays(-1), Make(start: today.AddDays(-1)).StartDate);
        Assert.Equal(today.AddDays(366), Make(start: today.AddDays(366)).StartDate);
        Assert.Throws<DomainException>(() => Make(start: today.AddDays(-2)));
        Assert.Throws<DomainException>(() => Make(start: today.AddDays(367)));
    }

    [Fact]
    public void The_range_follows_the_offset_so_a_browser_behind_utc_is_still_on_the_day_before()
    {
        // 03:00 UTC is 22:00 on the 3rd five hours behind.
        Assert.Equal((new DateOnly(2026, 10, 2), new DateOnly(2027, 10, 4)), Schedule.StartDateRange(Now, -300));
        Assert.Equal(new DateOnly(2026, 10, 2), Make(start: new DateOnly(2026, 10, 2), offset: -300).StartDate);
        Assert.Throws<DomainException>(() => Make(start: new DateOnly(2026, 10, 1), offset: -300));
    }

    [Theory]
    [InlineData(9999, 12, 31)]
    [InlineData(9999, 12, 30)]
    [InlineData(1, 1, 1)]
    [InlineData(2100, 1, 1)]
    public void A_start_date_that_breaks_the_calendar_is_refused_not_stored(int year, int month, int day)
    {
        var ex = Assert.Throws<DomainException>(() => Make(start: new DateOnly(year, month, day)));
        Assert.Contains("2026-10-03", ex.Message); // the message says what is allowed
        Assert.Contains("2027-10-05", ex.Message);
    }
}
