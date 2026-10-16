using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using static SIRIAUTOPOST.Application.Tests.SchedulingWorld;

namespace SIRIAUTOPOST.Application.Tests;

// "How many minutes apart per group" (2026-10-13): a schedule with its own gap keeps its posts that far apart, lays the
// rounds of a day on one line, and can lay out what is already queued again. "Now" is Saturday 3 Oct 2026, 10:30 in Bangkok.
public class ScheduleSpacingTests
{
    private static DateTimeOffset Bkk(int day, int hour, double minute = 0) =>
        new DateTimeOffset(2026, 10, day, hour, 0, 0, Bangkok).AddMinutes(minute);

    private static List<double> Gaps(IEnumerable<Post> posts)
    {
        var times = posts.Select(p => p.ScheduledAt).Order().ToList();
        return times.Zip(times.Skip(1), (a, b) => (b - a).TotalMinutes).ToList();
    }

    [Fact]
    public async Task Groups_of_a_round_are_never_closer_than_the_gap_and_never_much_further()
    {
        var w = new SchedulingWorld(links: 12);
        w.SetDelay(1, 2); // the smart delay is short: only the schedule's own gap can explain the distances below
        var s = w.NewSchedule(times: ["18:00"], groupGapMinutes: 10);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(7));

        Assert.Equal(12, w.Added.Count);
        Assert.Equal(Bkk(4, 18), w.Added.Min(p => p.ScheduledAt)); // the round opens at its slot
        Assert.All(Gaps(w.Added), gap => Assert.InRange(gap, 10, 12.5)); // 10 minutes plus up to a quarter, never less
    }

    [Fact]
    public async Task The_least_random_draw_gives_exactly_the_gap_and_the_most_gives_a_quarter_more()
    {
        var w = new SchedulingWorld(links: 3);
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["18:00"], groupGapMinutes: 8);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(0));
        Assert.Equal([Bkk(4, 18), Bkk(4, 18, 8), Bkk(4, 18, 16)], w.Added.Select(p => p.ScheduledAt));

        w.Added.Clear();
        await w.RunAsync(s, Today.AddDays(2), Today.AddDays(2), new FixedRandom(0.999999));
        var late = w.Added.Select(p => p.ScheduledAt).ToList();
        Assert.InRange((late[1] - late[0]).TotalMinutes, 9.99, 10);
        Assert.InRange((late[2] - late[1]).TotalMinutes, 9.99, 10);
    }

    [Fact]
    public async Task A_round_that_would_open_while_the_one_before_is_still_going_waits_for_it()
    {
        // Six groups 20 minutes apart take 100 minutes at least; the second time is only 30 minutes after the first.
        var w = new SchedulingWorld(links: 6);
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["09:00", "09:30"], groupGapMinutes: 20);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(0));

        var first = w.Added.Where(p => p.SlotKey!.EndsWith("T09:00")).Select(p => p.ScheduledAt).Order().ToList();
        var second = w.Added.Where(p => p.SlotKey!.EndsWith("T09:30")).Select(p => p.ScheduledAt).Order().ToList();
        Assert.Equal(Bkk(4, 9), first[0]);
        Assert.Equal(Bkk(4, 10, 40), first[^1]); // 5 gaps of 20 minutes
        Assert.Equal(Bkk(4, 11), second[0]); // one gap after the last group of the first round, not at 09:30
        Assert.All(Gaps(w.Added), gap => Assert.True(gap >= 20));
        Assert.Equal(12, w.Added.Count);
    }

    [Fact]
    public async Task The_next_day_starts_at_its_own_time_even_when_the_day_before_ran_long()
    {
        var w = new SchedulingWorld(links: 10);
        var s = w.NewSchedule(times: ["23:00"], groupGapMinutes: 60); // the round goes on for 9 hours, into the next day

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(2), new FixedRandom(0));

        var day2 = w.Added.Where(p => p.SlotKey!.StartsWith("2026-10-05")).Select(p => p.ScheduledAt).Min();
        Assert.Equal(Bkk(5, 23), day2); // the line starts over each day: no endless drift
    }

    [Fact]
    public async Task A_schedule_without_a_gap_keeps_the_smart_delay_and_overlapping_rounds()
    {
        var w = new SchedulingWorld(links: 3);
        w.SetDelay(5, 9);
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["09:00", "09:05"]);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(0.5));

        Assert.Equal(Bkk(4, 9, 5), w.Added.Where(p => p.SlotKey!.EndsWith("T09:05")).Min(p => p.ScheduledAt)); // not pushed
    }

    [Fact]
    public async Task The_start_now_round_and_the_regular_rounds_of_today_share_one_line()
    {
        var w = new SchedulingWorld(links: 4);
        var s = w.NewSchedule(times: ["10:40"], startNow: true, groupGapMinutes: 15);

        await w.RunAsync(s, Today, Today, new FixedRandom(0));

        var round = w.Added.Where(p => p.SlotKey!.EndsWith("now")).Select(p => p.ScheduledAt).Order().ToList();
        var regular = w.Added.Where(p => p.SlotKey!.EndsWith("T10:40")).Select(p => p.ScheduledAt).Order().ToList();
        Assert.Equal(4, round.Count);
        Assert.True(regular[0] >= round[^1].AddMinutes(15)); // 10:40 is inside the opening round: it waits for it
        Assert.All(Gaps(w.Added), gap => Assert.True(gap >= 15));
    }

    // ---- laying out what is queued ----

    [Fact]
    public async Task Reflow_spreads_a_plan_whose_rounds_overlap_and_keeps_the_order()
    {
        var w = new SchedulingWorld(links: 6);
        w.SetDelay(3, 4);
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["18:00", "18:10"]);
        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(0));
        // The rounds go one after the other, each in the order it had: the first round, then the second.
        var before = w.Added.OrderBy(p => p.SlotKey, StringComparer.Ordinal).ThenBy(p => p.ScheduledAt).Select(p => p.Id).ToList();
        Assert.Contains(Gaps(w.Added), gap => gap < 5); // a burst: two rounds of 3-minute steps on top of each other

        s.SetGroupGap(15);
        var moved = ScheduleSpacing.Reflow(s, w.Added, Now, null, () => 15 * 60);

        Assert.True(moved > 0);
        Assert.All(Gaps(w.Added), gap => Assert.True(gap >= 15));
        Assert.Equal(Bkk(4, 18), w.Added.Min(p => p.ScheduledAt)); // the first one stays at its slot
        Assert.Equal(before, w.Added.OrderBy(p => p.ScheduledAt).Select(p => p.Id)); // each round keeps its own order
    }

    [Fact]
    public async Task Reflow_starts_todays_backlog_after_now_and_one_gap_after_the_last_post_taken()
    {
        var w = new SchedulingWorld(links: 4);
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["09:00"], groupGapMinutes: 10);
        // Four posts that were due this morning and never went (the browser was off).
        await w.RunAsync(s, Today, Today, new FixedRandom(0), now: Bkk(3, 8));
        Assert.Equal(4, w.Added.Count);

        var moved = ScheduleSpacing.Reflow(s, w.Added, Now, Bkk(3, 10, 25), () => 10 * 60);

        Assert.Equal(4, moved);
        var times = w.Added.Select(p => p.ScheduledAt).Order().ToList();
        Assert.Equal(Bkk(3, 10, 35), times[0]); // 10 minutes after the browser last took a post (10:25), a bit past now (10:30)
        Assert.All(Gaps(w.Added), gap => Assert.Equal(10, gap));
    }

    [Fact]
    public async Task Reflow_keeps_the_start_now_round_ahead_of_the_regular_times_of_the_day()
    {
        var w = new SchedulingWorld(links: 4);
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["18:00"], startNow: true);
        await w.RunAsync(s, Today, Today, new FixedRandom(0));
        var now = w.Added.Where(p => p.SlotKey!.EndsWith("now")).OrderBy(p => p.ScheduledAt).Select(p => p.Id).ToList();
        var regular = w.Added.Where(p => p.SlotKey!.EndsWith("T18:00")).OrderBy(p => p.ScheduledAt).Select(p => p.Id).ToList();
        Assert.Equal(4, now.Count);

        s.SetGroupGap(20);
        ScheduleSpacing.Reflow(s, w.Added, Now, null, () => 20 * 60);

        var order = w.Added.OrderBy(p => p.ScheduledAt).Select(p => p.Id).ToList();
        Assert.Equal([.. now, .. regular], order); // the round of the click first, then 18:00
        Assert.All(Gaps(w.Added), gap => Assert.True(gap >= 20));
        Assert.True(w.Added.Where(p => now.Contains(p.Id)).Min(p => p.ScheduledAt) < Bkk(3, 11)); // it still starts right away
    }

    [Fact]
    public async Task Reflow_leaves_posts_that_somebody_rushed_or_that_a_browser_has_alone()
    {
        var w = new SchedulingWorld(links: 4);
        var s = w.NewSchedule(times: ["18:00"], groupGapMinutes: 10);
        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(0));
        var rushed = w.Added[1];
        rushed.RunNow(Now);
        var taken = w.Added[2];
        taken.Claim(Guid.NewGuid(), Now);
        var rushedAt = rushed.ScheduledAt;
        var takenAt = taken.ScheduledAt;

        s.SetGroupGap(30);
        ScheduleSpacing.Reflow(s, w.Added, Now, null, () => 30 * 60);

        Assert.Equal(rushedAt, rushed.ScheduledAt);
        Assert.Equal(takenAt, taken.ScheduledAt);
    }

    [Fact]
    public async Task Reflow_ignores_days_that_are_over()
    {
        var w = new SchedulingWorld(links: 3);
        var s = w.NewSchedule(times: ["09:00"], groupGapMinutes: 5);
        await w.RunAsync(s, Today, Today, new FixedRandom(0), now: Bkk(3, 8));
        var old = w.Added.Select(p => (Post: p, At: p.ScheduledAt)).ToList();

        // A day later today's slot belongs to the day before: nothing there is laid out again.
        var moved = ScheduleSpacing.Reflow(s, w.Added, Now.AddDays(1), null, () => 5 * 60);

        Assert.Equal(0, moved);
        Assert.All(old, o => Assert.Equal(o.At, o.Post.ScheduledAt));
    }

    // ---- the number itself ----

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void A_gap_outside_zero_to_one_hundred_twenty_minutes_is_refused(int minutes)
    {
        var w = new SchedulingWorld();
        Assert.Throws<DomainException>(() => w.NewSchedule(groupGapMinutes: minutes));
        Assert.Throws<DomainException>(() => w.NewSchedule().SetGroupGap(minutes));
    }

    [Fact]
    public void A_gap_can_be_set_and_taken_off_again()
    {
        var s = new SchedulingWorld().NewSchedule(groupGapMinutes: 7);
        Assert.Equal(7, s.GroupGapMinutes);
        s.SetGroupGap(0);
        Assert.Equal(0, s.GroupGapMinutes);
    }
}
