using NSubstitute;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using static SIRIAUTOPOST.Application.Tests.SchedulingWorld;

namespace SIRIAUTOPOST.Application.Tests;

// The schedule engine's pure behaviour: which posts a schedule makes for which days. "Now" is Saturday 3 Oct 2026, 10:30
// in Bangkok, and the schedules live in the Bangkok calendar (UTC+7).
public class ScheduleMaterializerTests
{
    private static readonly DateOnly Last = Today.AddDays(13); // today plus the 13 days after: the 14-day horizon

    private static DateTimeOffset Bkk(int day, int hour, double minute = 0) =>
        new DateTimeOffset(2026, 10, day, hour, 0, 0, Bangkok).AddMinutes(minute);

    [Fact]
    public async Task A_daily_schedule_makes_every_target_every_slot_for_fourteen_days_and_skips_what_is_past()
    {
        var w = new SchedulingWorld(links: 3);
        var s = w.NewSchedule(times: ["09:00", "18:00"]);

        var result = await w.RunAsync(s, Today, Last);

        // Today 09:00 (members at 09:00, 09:07:30, 09:15) is over at 10:30: 3 posts today, then 13 days of 2 slots x 3 links.
        Assert.Equal(3 + 13 * 6, result.Created);
        Assert.Equal(result.Created, w.Added.Count);
        Assert.All(w.Added, p => Assert.True(p.ScheduledAt > Now));
        Assert.All(w.Added, p => Assert.Equal((PostStatus.Queued, s.Id), (p.Status, p.ScheduleId)));
        Assert.Equal(Last, s.GeneratedThrough);
        Assert.Equal(Bkk(3, 18), result.FirstAt);
        Assert.Equal(Bkk(16, 18).AddMinutes(15), result.LastAt);
    }

    [Fact]
    public async Task The_targets_of_a_slot_follow_each_other_after_the_smart_delay()
    {
        var w = new SchedulingWorld(links: 3);
        w.SetDelay(5, 9);
        w.SetGroupShuffle(false); // the order of the set, so the times can be read in it
        var s = w.NewSchedule(times: ["18:00"]);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(0.5)); // 5 + 0.5 x 4 = 7 minutes apart

        Assert.Equal([Bkk(4, 18), Bkk(4, 18, 7), Bkk(4, 18, 14)], w.Added.Select(p => p.ScheduledAt));
        Assert.Equal(w.Links.Select(l => l.Id), w.Added.Select(p => p.LinkId!.Value));
        w.Added.Clear();
        await w.RunAsync(s, Today.AddDays(2), Today.AddDays(2), new FixedRandom(0)); // the shortest delay
        Assert.Equal([Bkk(5, 18), Bkk(5, 18, 5), Bkk(5, 18, 10)], w.Added.Select(p => p.ScheduledAt));
    }

    [Fact]
    public async Task A_slot_that_has_started_keeps_the_targets_that_are_still_to_come()
    {
        var w = new SchedulingWorld(links: 3); // 09:00, 09:07:30, 09:15
        w.SetGroupShuffle(false);
        var s = w.NewSchedule(times: ["09:00"]);

        var result = await w.RunAsync(s, Today, Today, now: Bkk(3, 9, 10));

        Assert.Equal(1, result.Created);
        Assert.Equal(w.Links[2].Id, w.Added.Single().LinkId);
        Assert.Equal(Bkk(3, 9, 15), w.Added.Single().ScheduledAt);
    }

    [Fact]
    public async Task The_groups_of_a_slot_go_out_one_by_one_in_a_random_order_with_the_smart_delay_between_them()
    {
        var w = new SchedulingWorld(links: 8);
        w.SetDelay(5, 9);
        var s = w.NewSchedule(times: ["18:00"]);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(11));

        var times = w.Added.Select(p => p.ScheduledAt).ToList();
        Assert.Equal(8, times.Count);
        Assert.Equal(Bkk(4, 18), times[0]); // the first group opens the slot
        for (var i = 1; i < times.Count; i++)
        {
            var gap = (times[i] - times[i - 1]).TotalMinutes;
            Assert.InRange(gap, 5, 9); // never together, never a robot's fixed beat
        }
        Assert.Equal(8, times.Distinct().Count());
        Assert.Equal(w.Links.Select(l => l.Id).Order(), w.Added.Select(p => p.LinkId!.Value).Order()); // every group once
        Assert.NotEqual(w.Links.Select(l => l.Id), w.Added.Select(p => p.LinkId!.Value)); // not in the order of the set
    }

    [Fact]
    public async Task Start_now_queues_a_round_that_begins_a_few_seconds_from_now_and_then_the_regular_times_go_on()
    {
        var w = new SchedulingWorld(links: 3);
        w.SetDelay(5, 9);
        var s = w.NewSchedule(times: ["09:00", "18:00"], startNow: true);
        Assert.True(s.StartNow);
        Assert.True(s.NowPending);

        var result = await w.RunAsync(s, Today, Today);

        var now = w.Added.Where(p => p.SlotKey == Schedule.SlotKey(Today, Schedule.NowSlot)).OrderBy(p => p.ScheduledAt).ToList();
        Assert.Equal(3, now.Count);
        Assert.InRange((now[0].ScheduledAt - Now).TotalSeconds, 2, 20);
        for (var i = 1; i < now.Count; i++) Assert.InRange((now[i].ScheduledAt - now[i - 1].ScheduledAt).TotalMinutes, 5, 9);
        Assert.Equal(w.Links.Select(l => l.Id).Order(), now.Select(p => p.LinkId!.Value).Order());
        Assert.Equal(3 + 3, result.Created); // the round now, and tonight's 18:00 (09:00 is over)
        Assert.All(w.Added, p => Assert.True(p.ScheduledAt > Now));
        Assert.False(s.NowPending);
        Assert.True(s.StartNow); // the choice stays on record
    }

    [Fact]
    public async Task The_start_now_round_is_made_only_once_even_when_the_schedule_is_run_again()
    {
        var w = new SchedulingWorld(links: 2);
        var s = w.NewSchedule(times: ["18:00"], startNow: true);
        await w.RunAsync(s, Today, Today);
        var first = w.Added.Count(p => p.SlotKey?.EndsWith("now") == true);
        Assert.Equal(2, first);

        // Paused and resumed a day later: the regular times come back, the opening round does not.
        s.SetActive(false);
        s.SetActive(true);
        w.Added.Clear();
        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), now: Now.AddDays(1));

        Assert.DoesNotContain(w.Added, p => p.SlotKey?.EndsWith("now") == true);
        Assert.Equal(2, w.Added.Count);
    }

    [Fact]
    public async Task A_once_schedule_that_starts_now_ignores_its_time_and_makes_only_the_round_now()
    {
        var w = new SchedulingWorld(links: 3);
        var s = w.NewSchedule(mode: ScheduleMode.Once, onceTime: "23:00", startNow: true, start: Today.AddDays(30));

        var result = await w.RunAsync(s, s.StartDate, s.StartDate);

        Assert.Equal(Today, s.StartDate); // today, whatever date was sent
        Assert.Equal(3, result.Created);
        Assert.All(w.Added, p => Assert.Equal(Schedule.SlotKey(Today, Schedule.NowSlot), p.SlotKey));
        Assert.All(w.Added, p => Assert.True(p.ScheduledAt < Bkk(3, 23)));
    }

    [Fact]
    public async Task Start_now_waits_while_nothing_can_be_posted()
    {
        var w = new SchedulingWorld(links: 0); // no group to post to
        var s = w.NewSchedule(times: ["18:00"], startNow: true);

        await w.RunAsync(s, Today, Today);

        Assert.True(s.NowPending); // the round is still owed when a group arrives
        Assert.Empty(w.Added);
    }

    [Fact]
    public async Task A_target_with_its_own_times_leaves_the_schedules_and_the_rest_stay_in_their_slot()
    {
        var w = new SchedulingWorld(links: 3);
        var overrides = new Dictionary<string, IReadOnlyList<string>> { [Schedule.LinkKey(w.Links[1].Id)] = ["12:00", "20:00"] };
        var s = w.NewSchedule(times: ["09:00"], overrides: overrides);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        var byLink = w.Added.GroupBy(p => p.LinkId).ToDictionary(g => g.Key!.Value, g => g.Select(p => Local(p).ToString("HH:mm")).ToList());
        Assert.Equal(["09:00"], byLink[w.Links[0].Id]);
        Assert.Equal(["12:00", "20:00"], byLink[w.Links[1].Id]);
        Assert.Equal(["09:07"], byLink[w.Links[2].Id]); // second in the 09:00 slot, 7.5 minutes after the first
    }

    [Fact]
    public async Task Once_makes_posts_only_for_its_day()
    {
        var w = new SchedulingWorld(links: 2);
        var s = w.NewSchedule(ScheduleMode.Once, start: Today.AddDays(1), onceTime: "14:00");

        var result = await w.RunAsync(s, Today, Last);

        Assert.Equal(2, result.Created);
        Assert.All(w.Added, p => Assert.Equal(new DateOnly(2026, 10, 4), DateOnly.FromDateTime(Local(p))));
        Assert.Equal(Bkk(4, 14), result.FirstAt);
    }

    [Fact]
    public async Task Once_on_a_day_that_is_over_makes_nothing()
    {
        var w = new SchedulingWorld(links: 2);
        var s = w.NewSchedule(ScheduleMode.Once, start: Today, onceTime: "09:00"); // 09:00 today is past at 10:30

        Assert.Equal(0, (await w.RunAsync(s, Today, Last)).Created);
    }

    [Fact]
    public async Task Weekend_is_friday_saturday_and_sunday()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule(ScheduleMode.Weekend, times: ["10:00"]); // today's 10:00 is over

        await w.RunAsync(s, Today, Last);

        // Sun 4, Fri 9, Sat 10, Sun 11, Fri 16.
        Assert.Equal([4, 9, 10, 11, 16], w.Added.Select(p => Local(p).Day));
        Assert.All(w.Added, p => Assert.Contains(Local(p).DayOfWeek, new[] { DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }));
    }

    [Fact]
    public async Task Weekdays_skip_the_weekend()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule(ScheduleMode.Weekdays, times: ["10:00"]);

        await w.RunAsync(s, Today, Last);

        Assert.Equal([5, 6, 7, 8, 9, 12, 13, 14, 15, 16], w.Added.Select(p => Local(p).Day));
    }

    [Fact]
    public async Task Interval_posts_every_n_hours_from_the_first_time_until_midnight()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule(ScheduleMode.Interval, everyHours: 6, firstTime: "06:00");

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal(["06:00", "12:00", "18:00"], w.Added.Select(p => Local(p).ToString("HH:mm")));
    }

    [Fact]
    public async Task Drip_spreads_its_posts_between_two_times()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule(ScheduleMode.Drip, dripFrom: "09:00", dripTo: "21:00", dripCount: 3);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal(["09:00", "15:00", "21:00"], w.Added.Select(p => Local(p).ToString("HH:mm")));
    }

    [Fact]
    public async Task The_schedules_calendar_decides_the_utc_time()
    {
        var w = new SchedulingWorld(links: 1);
        var bangkok = w.NewSchedule(times: ["09:00"], offsetMinutes: 420);
        await w.RunAsync(bangkok, Today.AddDays(1), Today.AddDays(1));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 2, 0, 0, TimeSpan.Zero), w.Added.Single().ScheduledAt);
        Assert.Equal(TimeSpan.Zero, w.Added.Single().ScheduledAt.Offset); // timestamptz only takes UTC

        w.Added.Clear();
        var newYork = w.NewSchedule(times: ["09:00"], offsetMinutes: -300);
        await w.RunAsync(newYork, Today.AddDays(1), Today.AddDays(1));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 14, 0, 0, TimeSpan.Zero), w.Added.Single().ScheduledAt);
    }

    // ---- which collection post ----

    [Fact]
    public async Task Rotate_takes_the_collections_posts_in_order_one_per_slot_for_everyone_in_it_and_keeps_the_place()
    {
        var w = new SchedulingWorld(links: 3, posts: 3);
        var s = w.NewSchedule(order: PostOrder.Rotate);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(4)); // 4 days, one slot each

        var bySlot = w.Added.GroupBy(p => p.SlotKey).Select(g => g.Select(p => p.CollectionPostId!.Value).Distinct().Single()).ToList();
        Assert.Equal([w.Posts[0].Id, w.Posts[1].Id, w.Posts[2].Id, w.Posts[0].Id], bySlot);
        Assert.Equal(4, s.Cursor);

        w.Added.Clear();
        await w.RunAsync(s, Today.AddDays(5), Today.AddDays(6)); // the next run goes on where this one stopped
        Assert.Equal([w.Posts[1].Id, w.Posts[2].Id], w.Added.GroupBy(p => p.SlotKey).Select(g => g.First().CollectionPostId!.Value));
        Assert.Equal(6, s.Cursor);
    }

    [Fact]
    public async Task Rotate_does_not_spend_a_post_on_a_slot_that_made_nothing()
    {
        var w = new SchedulingWorld(links: 1, posts: 3);
        var s = w.NewSchedule(times: ["09:00", "18:00"], order: PostOrder.Rotate);

        await w.RunAsync(s, Today, Today); // today's 09:00 is over: 18:00 gets the first post

        Assert.Equal(w.Posts[0].Id, w.Added.Single().CollectionPostId);
        Assert.Equal(1, s.Cursor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Shuffle_never_gives_a_link_the_post_it_had_just_before(int seed)
    {
        var w = new SchedulingWorld(links: 2, posts: 4);
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today, Last, new SeededRandom(seed));

        foreach (var link in w.Links)
        {
            var seq = w.Added.Where(p => p.LinkId == link.Id).OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId).ToList();
            Assert.Equal(14, seq.Count); // today's 18:00 is still to come
            for (var i = 1; i < seq.Count; i++) Assert.NotEqual(seq[i - 1], seq[i]);
        }
        Assert.Equal(4, w.Added.Select(p => p.CollectionPostId).Distinct().Count()); // and every post gets used
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Shuffle_keeps_the_last_n_posts_away_from_a_link_when_the_collection_is_big_enough(int seed)
    {
        var w = new SchedulingWorld(links: 1, posts: 7);
        w.SetAdvanced(a => a.RecentAvoid = 3);
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today, Last, new SeededRandom(seed));

        var seq = w.Added.OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId).ToList();
        for (var i = 0; i < seq.Count; i++)
            for (var back = 1; back <= 3 && i - back >= 0; back++) Assert.NotEqual(seq[i - back], seq[i]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Shuffle_continues_from_what_the_link_had_in_the_database(int seed)
    {
        var w = new SchedulingWorld(links: 1, posts: 4);
        w.SetAdvanced(a => a.RecentAvoid = 2);
        w.History[w.Links[0].Id] = [w.Posts[0].Id, w.Posts[1].Id]; // newest first
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(seed));

        Assert.Contains(w.Added.Single().CollectionPostId, new Guid?[] { w.Posts[2].Id, w.Posts[3].Id });
        await w.PostRepo.Received().ListRecentCollectionPostIdsByLinkAsync(Arg.Any<IEnumerable<Guid>>(), 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_few_posts_a_link_only_avoids_the_one_it_just_had()
    {
        var w = new SchedulingWorld(links: 1, posts: 3);
        w.SetAdvanced(a => a.RecentAvoid = 10); // more than the collection has: avoiding 10 is impossible
        w.History[w.Links[0].Id] = [w.Posts[0].Id, w.Posts[1].Id];
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        for (var seed = 1; seed <= 8; seed++)
        {
            w.Added.Clear();
            await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(seed));
            Assert.NotEqual(w.Posts[0].Id, w.Added.Single().CollectionPostId);
        }
    }

    [Fact]
    public async Task One_post_is_all_a_shuffle_has_to_give()
    {
        var w = new SchedulingWorld(links: 1, posts: 1);
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today, Last, new SeededRandom(3));

        Assert.Equal(14, w.Added.Count);
        Assert.All(w.Added, p => Assert.Equal(w.Posts[0].Id, p.CollectionPostId));
    }

    // The group is what a slot is built around: every group gets ONE post per slot, picked at random from the collection,
    // never every post of the collection.

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Shuffle_gives_every_group_one_post_a_slot_and_different_groups_different_posts(int seed)
    {
        var w = new SchedulingWorld(links: 6, posts: 20);
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(seed)); // one slot, 6 groups

        Assert.Equal(6, w.Added.Count); // not 6 x 20
        foreach (var link in w.Links) Assert.Single(w.Added, p => p.LinkId == link.Id);
        Assert.Equal(6, w.Added.Select(p => p.CollectionPostId).Distinct().Count()); // nobody posts the same text in the same round
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Shuffle_with_fewer_posts_than_groups_still_gives_each_group_one_post_and_uses_them_all(int seed)
    {
        var w = new SchedulingWorld(links: 5, posts: 3);
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(seed));

        Assert.Equal(5, w.Added.Count);
        foreach (var link in w.Links) Assert.Single(w.Added, p => p.LinkId == link.Id);
        Assert.Equal(3, w.Added.Select(p => p.CollectionPostId).Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Shuffle_spreads_the_posts_over_the_groups_across_slots_and_days_without_repeating_for_a_group(int seed)
    {
        var w = new SchedulingWorld(links: 4, posts: 12);
        var s = w.NewSchedule(times: ["09:00", "18:00"], order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(3), new SeededRandom(seed)); // 3 days x 2 slots x 4 groups

        Assert.Equal(24, w.Added.Count);
        foreach (var slot in w.Added.GroupBy(p => p.SlotKey))
            Assert.Equal(4, slot.Select(p => p.CollectionPostId).Distinct().Count()); // a round never repeats a text
        foreach (var link in w.Links)
        {
            var seq = w.Added.Where(p => p.LinkId == link.Id).OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId).ToList();
            Assert.Equal(6, seq.Count);
            Assert.True(seq.Distinct().Count() > 1, "a group is not given one post again and again");
            for (var i = 1; i < seq.Count; i++) Assert.NotEqual(seq[i - 1], seq[i]);
        }
    }

    // ---- may a group get a post again? ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Repeat_any_lets_a_group_get_the_same_post_again_even_within_a_day(int seed)
    {
        var w = new SchedulingWorld(links: 1, posts: 2);
        var s = w.NewSchedule(times: ["09:00", "13:00", "18:00"], order: PostOrder.Shuffle, repeat: PostRepeat.Any);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(8), new SeededRandom(seed)); // 8 days x 3 slots, 2 posts

        var seq = w.Added.OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId).ToList();
        Assert.Equal(24, seq.Count);
        Assert.Contains(true, seq.Zip(seq.Skip(1), (a, b) => a == b)); // the same post twice in a row happens
        await w.PostRepo.DidNotReceive().ListRecentCollectionPostIdsByLinkAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeat_any_still_gives_the_groups_of_one_round_different_posts()
    {
        var w = new SchedulingWorld(links: 6, posts: 20);
        var s = w.NewSchedule(order: PostOrder.Shuffle, repeat: PostRepeat.Any);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(5));

        Assert.Equal(6, w.Added.Select(p => p.CollectionPostId).Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Repeat_never_gives_a_group_each_post_once_before_any_comes_again(int seed)
    {
        var w = new SchedulingWorld(links: 1, posts: 5);
        w.AllHistory[w.Links[0].Id] = [w.Posts[0].Id, w.Posts[1].Id]; // it has had two of the five
        var s = w.NewSchedule(order: PostOrder.Shuffle, repeat: PostRepeat.Never);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(7), new SeededRandom(seed)); // 7 posts for one group

        var seq = w.Added.OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId!.Value).ToList();
        Assert.Equal(7, seq.Count);
        Assert.Equal(new[] { w.Posts[2].Id, w.Posts[3].Id, w.Posts[4].Id }.Order(), seq.Take(3).Order()); // the three it has not had come first
        Assert.Equal(5, seq.Take(8).Distinct().Count()); // then it starts over: every post is used
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Repeat_never_holds_for_every_group_of_many(int seed)
    {
        var w = new SchedulingWorld(links: 4, posts: 12);
        var s = w.NewSchedule(times: ["09:00", "18:00"], order: PostOrder.Shuffle, repeat: PostRepeat.Never);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(3), new SeededRandom(seed)); // 6 posts per group of the 12

        foreach (var link in w.Links)
        {
            var seq = w.Added.Where(p => p.LinkId == link.Id).Select(p => p.CollectionPostId).ToList();
            Assert.Equal(6, seq.Count);
            Assert.Equal(6, seq.Distinct().Count());
        }
    }

    [Fact]
    public async Task Repeat_never_after_every_post_was_had_starts_over_without_giving_the_last_one_again()
    {
        var w = new SchedulingWorld(links: 1, posts: 3);
        w.AllHistory[w.Links[0].Id] = w.Posts.Select(p => p.Id).ToList();
        w.History[w.Links[0].Id] = [w.Posts[0].Id]; // the newest
        var s = w.NewSchedule(order: PostOrder.Shuffle, repeat: PostRepeat.Never);

        for (var seed = 1; seed <= 8; seed++)
        {
            w.Added.Clear();
            await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(seed));
            Assert.NotEqual(w.Posts[0].Id, w.Added.Single().CollectionPostId);
        }
    }

    [Fact]
    public async Task Repeat_recent_is_the_default()
    {
        var w = new SchedulingWorld(links: 1, posts: 3);
        Assert.Equal(PostRepeat.Recent, w.NewSchedule(order: PostOrder.Shuffle).Repeat);
        await Task.CompletedTask;
    }

    // ---- what a post looks like ----

    [Fact]
    public async Task A_post_is_composed_for_its_group_with_the_code_footer_and_hashtags()
    {
        var w = new SchedulingWorld(links: 2, posts: 0);
        w.AddPost("สินค้าใหม่ {{code}} มาแล้ว");
        w.Collection.Update(w.Collection.Name, null, null, new CollectionSettings { Footer = "สั่งซื้อ 081-234-5678", Hashtags = "#sale" });
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        var first = w.Added[0];
        Assert.Equal("สินค้าใหม่ A1 มาแล้ว\n\nสั่งซื้อ 081-234-5678\n#sale", first.Content);
        Assert.Equal(("A1", w.Links[0].Url, w.Links[0].Name, w.Page.Id), (first.Code, first.TargetUrl, first.Target, first.AccountId));
        Assert.Equal(Platform.Fb, first.Platform);
        Assert.Equal($"link:{w.Links[0].Id:N}", first.TargetKey);
        Assert.Equal("2026-10-04T18:00", first.SlotKey);
        Assert.Equal("สินค้าใหม่ B1 มาแล้ว\n\nสั่งซื้อ 081-234-5678\n#sale", w.Added[1].Content);
    }

    [Fact]
    public async Task Without_a_code_tag_the_code_is_the_first_line_and_a_link_without_a_code_adds_nothing()
    {
        var w = new SchedulingWorld(links: 0, posts: 1);
        w.AddLink("coded", "รหัส9");
        w.AddLink("plain");
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal("รหัส9\nโพสต์ 1", w.Added[0].Content);
        Assert.Equal("โพสต์ 1", w.Added[1].Content);
        Assert.Null(w.Added[1].Code);
    }

    [Fact]
    public async Task A_post_carries_the_media_of_its_collection_post_and_the_run_reports_them_once()
    {
        var w = new SchedulingWorld(links: 2, posts: 0);
        var (m1, m2) = (Guid.NewGuid(), Guid.NewGuid());
        w.AddPost("มีรูป", m1, m2);
        var s = w.NewSchedule();

        var result = await w.RunAsync(s, Today, Last);

        Assert.All(w.Added, p => Assert.Equal([m1, m2], p.MediaIds));
        Assert.Equal([m1, m2], result.MediaIds);
    }

    [Fact]
    public async Task Other_accounts_of_the_set_post_to_their_default_target_without_a_code_or_an_address()
    {
        var w = new SchedulingWorld(links: 1, posts: 1);
        var ig = w.AddOtherAccount();
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal(2, w.Added.Count);
        var other = w.Added.Single(p => p.AccountId == ig.Id);
        Assert.Equal(("ฟีด", Platform.Ig, null, null, null, $"account:{ig.Id:N}"), (other.Target, other.Platform, other.TargetUrl, other.Code, other.LinkId, other.TargetKey));
        Assert.Equal("โพสต์ 1", other.Content);
        Assert.Equal(w.Page.Id, w.Added.Single(p => p.LinkId == w.Links[0].Id).AccountId);
        Assert.Equal(TimeSpan.FromMinutes(7.5), other.ScheduledAt - w.Added[0].ScheduledAt); // after the link in the same slot
    }

    [Fact]
    public async Task Links_that_are_off_invalid_or_repeated_are_not_posted_to()
    {
        var w = new SchedulingWorld(links: 0, posts: 1);
        var good = w.AddLink("good");
        var off = w.AddLink("off");
        off.DisableManually();
        w.AddLink("dupe").Edit("", "https://www.facebook.com/groups/good", "", 0, true);
        var bad = SetLink.Create(w.Ws.Id, w.Set.Id, "x", "https://example.com/nope", "", 0, Now, 9);
        w.Links.Add(bad);
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal(good.Id, w.Added.Single().LinkId);
    }

    [Fact]
    public async Task Nothing_is_made_without_a_connected_account_to_post_the_links()
    {
        var w = new SchedulingWorld(links: 2, posts: 1);
        w.Accounts.Clear();
        w.Accounts.Add(SocialAccount.Create(w.Ws.Id, Platform.Fb, "Page", "", "เพจ"));
        var s = w.NewSchedule();

        var result = await w.RunAsync(s, Today, Last);

        Assert.Equal(0, result.Created);
        Assert.Empty(w.Added);
        Assert.Null(s.GeneratedThrough);
    }

    [Fact]
    public async Task The_account_a_set_names_posts_its_links_even_when_it_is_not_the_first()
    {
        var w = new SchedulingWorld(links: 1, posts: 1);
        var second = SocialAccount.Connect(w.Ws.Id, Device.Pair(w.Ws.Id, "Laptop", "", "", "h2", Now), 1);
        w.Accounts.Add(second);
        w.Set.Update(w.Set.Name, second.Id, []);
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal(second.Id, w.Added.Single().AccountId);
    }

    [Fact]
    public async Task Posts_that_are_not_approved_yet_are_not_used_and_a_schedule_without_any_stays_unmarked()
    {
        var w = new SchedulingWorld(links: 1, posts: 0);
        w.Collection.Update(w.Collection.Name, null, null, new CollectionSettings { RequireApproval = true });
        w.AddPost("แบบร่าง"); // a draft
        var s = w.NewSchedule();

        var result = await w.RunAsync(s, Today, Last);

        Assert.Equal((0, 0), (result.Created, result.UsablePosts));
        Assert.Null(s.GeneratedThrough); // a later run fills it in once something is approved

        var approved = w.AddPost("อนุมัติแล้ว");
        approved.RequestApproval(Now);
        approved.Approve(Now);
        var again = await w.RunAsync(s, Today, Last);
        Assert.Equal(1, again.UsablePosts);
        Assert.All(w.Added, p => Assert.Equal(approved.Id, p.CollectionPostId));
    }

    // ---- idempotence and limits ----

    [Fact]
    public async Task Running_again_adds_only_what_is_missing()
    {
        var w = new SchedulingWorld(links: 3);
        var s = w.NewSchedule();
        await w.RunAsync(s, Today, Last);
        var first = w.Added.ToList();
        Assert.Equal(14 * 3, first.Count);

        // Everything exists: nothing is made.
        w.ExistingKeys.AddRange(first.Select(p => (p.TargetKey!, p.SlotKey!)));
        w.Added.Clear();
        Assert.Equal(0, (await w.RunAsync(s, Today, Last)).Created);

        // Two posts were deleted since: exactly those come back.
        w.ExistingKeys.Remove((first[4].TargetKey!, first[4].SlotKey!));
        w.ExistingKeys.Remove((first[20].TargetKey!, first[20].SlotKey!));
        var result = await w.RunAsync(s, Today, Last);
        Assert.Equal(2, result.Created);
        Assert.Equal([(first[4].TargetKey, first[4].SlotKey), (first[20].TargetKey, first[20].SlotKey)],
            w.Added.Select(p => (p.TargetKey, p.SlotKey)));
    }

    [Fact]
    public async Task The_run_asks_for_the_existing_posts_from_the_start_of_its_first_local_day()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(2), Today.AddDays(3));

        await w.PostRepo.Received(1).ListScheduleKeysAsync(s.Id, new DateTimeOffset(2026, 10, 4, 17, 0, 0, TimeSpan.Zero), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_run_of_more_than_two_thousand_posts_is_refused_and_leaves_nothing_behind()
    {
        var w = new SchedulingWorld(links: 0, posts: 1);
        for (var i = 0; i < 150; i++) w.AddLink($"g{i}");
        var s = w.NewSchedule(); // 150 links x 14 days = 2,100

        var ex = await Assert.ThrowsAsync<DomainException>(() => w.RunAsync(s, Today.AddDays(1), Today.AddDays(14)));

        Assert.Contains("2,000", ex.Message);
        Assert.Empty(w.Added);
        Assert.Null(s.GeneratedThrough);
    }

    [Fact]
    public async Task The_same_group_spelled_in_another_case_gets_one_post_per_slot_not_two()
    {
        var w = new SchedulingWorld(links: 0, posts: 1);
        w.AddLink("ABC", "C1");
        w.AddLink("abc", "C2"); // the same group: Facebook does not tell the two spellings apart
        w.AddLink("other", "C3");
        var s = w.NewSchedule();

        var result = await w.RunAsync(s, Today, Today);

        Assert.Equal(2, result.Created); // today's 18:00, two groups
        Assert.Equal(["C1", "C3"], w.Added.Select(p => p.Code).Order());
        Assert.Equal("https://www.facebook.com/groups/ABC", w.Added.First(p => p.Code == "C1").TargetUrl); // the first one keeps its spelling
    }

    [Fact]
    public async Task A_run_that_would_take_the_workspace_over_its_queue_limit_is_refused_and_says_how_many_are_queued()
    {
        var w = new SchedulingWorld(links: 2, posts: 1);
        var s = w.NewSchedule(); // 2 links x 14 days = 28 posts
        w.PostRepo.CountQueuedFutureAsync(w.Ws.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(9_973);

        var ex = await Assert.ThrowsAsync<QueueFullException>(() => w.RunAsync(s, Today, Today.AddDays(13)));

        Assert.Equal((9_973, 28), (ex.Queued, ex.Adding));
        Assert.Contains("9,973", ex.Message);
        Assert.Contains("10,000", ex.Message);
        Assert.Empty(w.Added);
        Assert.Null(s.GeneratedThrough);
        Assert.Equal(0, s.Cursor);
    }

    [Fact]
    public async Task Filling_the_queue_exactly_is_allowed_and_a_run_that_makes_nothing_is_never_refused()
    {
        var w = new SchedulingWorld(links: 2, posts: 1);
        var s = w.NewSchedule();
        w.PostRepo.CountQueuedFutureAsync(w.Ws.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(9_972);

        Assert.Equal(28, (await w.RunAsync(s, Today, Today.AddDays(13))).Created); // 9,972 + 28 = 10,000

        w.PostRepo.CountQueuedFutureAsync(w.Ws.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(10_000);
        w.ExistingKeys.AddRange(w.Added.Select(p => (p.TargetKey!, p.SlotKey!))); // everything is there already
        Assert.Equal(0, (await w.RunAsync(s, Today, Today.AddDays(13))).Created);
    }

    [Fact]
    public async Task Just_under_the_limit_is_fine()
    {
        var w = new SchedulingWorld(links: 0, posts: 1);
        for (var i = 0; i < 142; i++) w.AddLink($"g{i}"); // 142 x 14 = 1,988
        var s = w.NewSchedule();

        Assert.Equal(1988, (await w.RunAsync(s, Today.AddDays(1), Today.AddDays(14))).Created);
    }

    [Fact]
    public async Task A_post_too_long_once_composed_stops_the_run_with_a_message_saying_what_to_shorten()
    {
        var w = new SchedulingWorld(links: 0, posts: 0);
        w.AddLink("group0", new string('C', 100));
        w.AddPost(new string('ก', 4800) + string.Concat(Enumerable.Repeat("{{code}}", 25)));
        var s = w.NewSchedule();

        var ex = await Assert.ThrowsAsync<DomainException>(() => w.RunAsync(s, Today, Last));

        Assert.Contains("7000", ex.Message);
        Assert.Empty(w.Added);
    }

    [Fact]
    public async Task The_delay_between_targets_uses_the_workspaces_smart_delay()
    {
        var w = new SchedulingWorld(links: 2);
        w.SetDelay(10, 20);
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new FixedRandom(1.0));

        Assert.Equal(TimeSpan.FromMinutes(20), w.Added[1].ScheduledAt - w.Added[0].ScheduledAt);
    }

    [Fact]
    public async Task A_run_for_days_before_or_after_the_range_is_empty()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule(start: Today.AddDays(30));

        Assert.Equal(0, (await w.RunAsync(s, Today, Last)).Created); // not started yet
        Assert.Equal(0, (await w.RunAsync(s, Last, Today)).Created); // an upside-down range
    }

    [Fact]
    public async Task Generated_through_never_moves_back()
    {
        var w = new SchedulingWorld(links: 1);
        var s = w.NewSchedule();
        await w.RunAsync(s, Today, Last);
        Assert.Equal(Last, s.GeneratedThrough);

        await w.RunAsync(s, Today, Today.AddDays(2));

        Assert.Equal(Last, s.GeneratedThrough);
    }

    // ---- a post's own limits ----

    private static void Limit(CollectionPost p, Action<CollectionPostSettings> change)
    {
        var settings = p.Settings.Copy();
        change(settings);
        p.UpdateSettings(settings, Now);
    }

    [Fact]
    public async Task A_post_limited_to_the_evening_is_only_made_for_slots_in_its_window()
    {
        var w = new SchedulingWorld(links: 1, posts: 0);
        var evening = w.AddPost("โปรเย็น");
        Limit(evening, s => (s.TimeFrom, s.TimeTo) = ("17:00", "20:00"));
        var s = w.NewSchedule(times: ["09:00", "18:00"]);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(2));

        Assert.Equal(["2026-10-04T18:00", "2026-10-05T18:00"], w.Added.Select(p => p.SlotKey));
    }

    [Fact]
    public async Task A_post_limited_to_a_weekday_and_a_date_range_is_only_made_inside_them()
    {
        var w = new SchedulingWorld(links: 1, posts: 0);
        var monday = w.AddPost("โปรวันจันทร์");
        Limit(monday, s => (s.Weekdays, s.ValidUntil) = ([1], Today.AddDays(8)));
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Last); // Sun 4 Oct ... Fri 16 Oct

        // Mondays are 5 and 12 Oct; the second is after the post's last day (11 Oct)? No: 12 > 11, so only the 5th.
        Assert.Equal(["2026-10-05T18:00"], w.Added.Select(p => p.SlotKey));
    }

    [Fact]
    public async Task A_post_that_is_switched_off_is_never_made()
    {
        var w = new SchedulingWorld(links: 1, posts: 2);
        w.Posts[0].SetActive(false, Now);
        var s = w.NewSchedule(order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(6));

        Assert.NotEmpty(w.Added);
        Assert.All(w.Added, p => Assert.Equal(w.Posts[1].Id, p.CollectionPostId));
    }

    [Fact]
    public async Task Rotate_goes_past_a_post_that_does_not_allow_the_slot_and_comes_back_to_it_later()
    {
        var w = new SchedulingWorld(links: 1, posts: 2);
        Limit(w.Posts[0], s => (s.TimeFrom, s.TimeTo) = ("17:00", "20:00"));
        var s = w.NewSchedule(times: ["09:00", "18:00"], order: PostOrder.Rotate);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        // 09:00: the first post does not allow it, so the second goes; 18:00: the turn is back at the first.
        Assert.Equal([w.Posts[1].Id, w.Posts[0].Id], w.Added.OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId!.Value));
    }

    [Fact]
    public async Task A_post_with_a_daily_limit_is_used_that_many_times_a_day_over_all_groups()
    {
        var w = new SchedulingWorld(links: 4, posts: 2);
        Limit(w.Posts[0], s => s.MaxPerDay = 1);
        var s = w.NewSchedule(times: ["18:00"], order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(7));

        Assert.Equal(4, w.Added.Count);
        Assert.True(w.Added.Count(p => p.CollectionPostId == w.Posts[0].Id) <= 1);
    }

    [Fact]
    public async Task What_a_post_already_has_queued_that_day_counts_against_its_daily_limit()
    {
        var w = new SchedulingWorld(links: 2, posts: 2);
        Limit(w.Posts[0], s => s.MaxPerDay = 1);
        w.PostRepo.ListScheduledAtByCollectionPostAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([(w.Posts[0].Id, Bkk(4, 12))]); // one use on the 4th already
        var s = w.NewSchedule(times: ["18:00"], order: PostOrder.Shuffle);

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1), new SeededRandom(3));

        Assert.All(w.Added, p => Assert.Equal(w.Posts[1].Id, p.CollectionPostId));
    }

    [Fact]
    public async Task A_slot_no_post_allows_makes_nothing_and_does_not_stall_the_schedule()
    {
        var w = new SchedulingWorld(links: 1, posts: 1);
        Limit(w.Posts[0], s => (s.TimeFrom, s.TimeTo) = ("06:00", "07:00"));
        var s = w.NewSchedule(times: ["18:00"]);

        var result = await w.RunAsync(s, Today.AddDays(1), Today.AddDays(3));

        Assert.Equal(0, result.Created);
        Assert.True(result.Advanced); // the days were looked at: the top-up does not retry them at once
        Assert.Equal(Today.AddDays(3), s.GeneratedThrough);
    }

    [Fact]
    public async Task A_post_with_its_own_hashtags_and_footer_is_composed_with_them()
    {
        var w = new SchedulingWorld(links: 1, posts: 0);
        var own = w.AddPost("สินค้า");
        w.Collection.Update(w.Collection.Name, null, null, new CollectionSettings { Footer = "ท้ายชุด", Hashtags = "#ชุด" });
        Limit(own, s => (s.Footer, s.Hashtags) = ("ท้ายโพสต์", "#โพสต์"));
        var s = w.NewSchedule();

        await w.RunAsync(s, Today.AddDays(1), Today.AddDays(1));

        Assert.Equal("A1\nสินค้า\n\nท้ายโพสต์\n#โพสต์", w.Added.Single().Content); // the group code is the first line
    }
}
