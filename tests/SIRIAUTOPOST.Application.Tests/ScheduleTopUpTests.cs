using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using static SIRIAUTOPOST.Application.Tests.SchedulingWorld;

namespace SIRIAUTOPOST.Application.Tests;

public class ScheduleTopUpTests
{
    private static readonly DateOnly Last = Today.AddDays(13);

    // ---- which days are still to make ----

    [Fact]
    public void A_new_schedule_is_made_from_today_to_the_end_of_the_horizon()
    {
        var s = new SchedulingWorld().NewSchedule();
        Assert.Equal((Today, Last), ScheduleTopUp.Window(s, Now));
    }

    [Fact]
    public void A_schedule_that_starts_later_is_made_from_its_start_and_one_that_starts_beyond_the_horizon_waits()
    {
        var w = new SchedulingWorld();
        Assert.Equal((Today.AddDays(5), Last), ScheduleTopUp.Window(w.NewSchedule(start: Today.AddDays(5)), Now));
        Assert.Null(ScheduleTopUp.Window(w.NewSchedule(start: Today.AddDays(14)), Now));
    }

    [Fact]
    public void A_schedule_is_topped_up_from_the_day_after_what_it_has_and_never_from_the_past()
    {
        var w = new SchedulingWorld();
        var s = w.NewSchedule();
        s.MarkGenerated(Today.AddDays(5), 0);
        Assert.Equal((Today.AddDays(6), Last), ScheduleTopUp.Window(s, Now));

        s.MarkGenerated(Last, 0);
        Assert.Null(ScheduleTopUp.Window(s, Now)); // up to date
        Assert.Equal((Last.AddDays(1), Last.AddDays(1)), ScheduleTopUp.Window(s, Now.AddDays(1))); // a day later: one more day

        var stale = w.NewSchedule();
        stale.MarkGenerated(Today.AddDays(-20), 0);
        Assert.Equal((Today, Last), ScheduleTopUp.Window(stale, Now)); // old days are not made now
    }

    [Fact]
    public void The_day_is_the_schedules_own_local_day()
    {
        var w = new SchedulingWorld();
        var s = w.NewSchedule(offsetMinutes: -300, start: new DateOnly(2026, 10, 2));
        Assert.Equal(new DateOnly(2026, 10, 2), s.LocalDay(Now)); // 03:30 UTC is 22:30 of the 2nd five hours behind
        Assert.Equal((new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 15)), ScheduleTopUp.Window(s, Now));
    }

    [Fact]
    public void Once_is_made_one_time_on_its_day()
    {
        var w = new SchedulingWorld();
        var tomorrow = w.NewSchedule(ScheduleMode.Once, start: Today.AddDays(1));
        Assert.Equal((Today.AddDays(1), Today.AddDays(1)), ScheduleTopUp.Window(tomorrow, Now));
        tomorrow.MarkGenerated(Today.AddDays(1), 0);
        Assert.Null(ScheduleTopUp.Window(tomorrow, Now));

        Assert.Null(ScheduleTopUp.Window(w.NewSchedule(ScheduleMode.Once, start: Today.AddDays(-1)), Now)); // its day is over
    }

    // ---- topping up ----

    private sealed class Rig
    {
        public SchedulingWorld World { get; } = new(links: 2, posts: 2);
        public IWorkspaceRepository Workspaces { get; } = Substitute.For<IWorkspaceRepository>();
        public IScheduleRepository Schedules { get; } = Substitute.For<IScheduleRepository>();
        public IMediaRepository Media { get; } = Substitute.For<IMediaRepository>();
        public IUnitOfWork Uow { get; } = Substitute.For<IUnitOfWork>();
        public List<Schedule> Active { get; } = [];

        public Rig()
        {
            Workspaces.GetByIdAsync(World.Ws.Id, Arg.Any<CancellationToken>()).Returns(World.Ws);
            Schedules.ListActiveAsync(World.Ws.Id, Arg.Any<CancellationToken>()).Returns(_ => Active.Where(s => s.Active).ToList());
            Schedules.GetAsync(World.Ws.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(c => Active.FirstOrDefault(s => s.Id == c.ArgAt<Guid>(1)));
            // Discarding the unsaved changes means the schedule is read again as it is in the database: not generated yet.
            Uow.When(u => u.DiscardChanges()).Do(_ => Active.ForEach(s => s.ResetGenerated()));
        }

        public ScheduleTopUp TopUp(DateTimeOffset? now = null) =>
            new(Workspaces, Schedules, World.Materializer(), Media, Uow, new FixedClock(now ?? Now));

        public Schedule Add(Schedule s)
        {
            Active.Add(s);
            return s;
        }
    }

    [Fact]
    public async Task Nothing_to_do_costs_one_query()
    {
        var rig = new Rig();
        var done = rig.Add(rig.World.NewSchedule());
        done.MarkGenerated(Last, 0);

        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id));

        await rig.Schedules.Received(1).ListActiveAsync(rig.World.Ws.Id, Arg.Any<CancellationToken>());
        await rig.Workspaces.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await rig.Uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_schedule_that_is_behind_is_filled_saved_and_its_media_counted_once()
    {
        var rig = new Rig();
        var (m1, m2) = (Guid.NewGuid(), Guid.NewGuid());
        rig.World.Posts.Clear();
        rig.World.AddPost("มีรูป", m1, m2);
        var s = rig.Add(rig.World.NewSchedule());

        var created = await rig.TopUp().EnsureAsync(rig.World.Ws.Id);

        Assert.Equal(14 * 2, created);
        Assert.Equal(Last, s.GeneratedThrough);
        await rig.Uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await rig.Media.Received(1).RecordUseAsync(rig.World.Ws.Id, Arg.Is<IEnumerable<Guid>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { m1, m2 }.OrderBy(i => i))), Arg.Any<CancellationToken>());

        // The next call has nothing left to do.
        rig.World.Added.Clear();
        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id));
        Assert.Empty(rig.World.Added);
    }

    [Fact]
    public async Task The_next_day_adds_just_the_new_day()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        await rig.TopUp().EnsureAsync(rig.World.Ws.Id);
        rig.World.Added.Clear();
        rig.World.ExistingKeys.AddRange(Array.Empty<(string, string)>());

        var created = await rig.TopUp(Now.AddDays(1)).EnsureAsync(rig.World.Ws.Id);

        Assert.Equal(2, created); // the 17th, two links, one slot
        Assert.All(rig.World.Added, p => Assert.Equal("2026-10-17T18:00", p.SlotKey));
        Assert.Equal(Last.AddDays(1), s.GeneratedThrough);
    }

    [Fact]
    public async Task A_paused_schedule_is_left_alone()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        s.SetActive(false);

        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id));
        Assert.Empty(rig.World.Added);
    }

    [Fact]
    public async Task A_once_schedule_whose_day_is_over_is_switched_off_not_made()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule(ScheduleMode.Once, start: Today.AddDays(-1)));

        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id));

        Assert.False(s.Active);
        await rig.Uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Empty(rig.World.Added);
    }

    [Fact]
    public async Task A_schedule_that_cannot_be_made_does_not_stop_the_others()
    {
        var rig = new Rig();
        var big = LinkSet.Create(rig.World.Ws.Id, "ใหญ่", null, Now);
        var bigLinks = Enumerable.Range(0, 150).Select(i => SetLink.Create(rig.World.Ws.Id, big.Id, $"g{i}", $"https://www.facebook.com/groups/big{i}", "", 0, Now, i)).ToList();
        rig.World.LinkSets.GetAsync(rig.World.Ws.Id, big.Id, Arg.Any<CancellationToken>()).Returns(big);
        rig.World.SetLinks.ListBySetAsync(rig.World.Ws.Id, big.Id, Arg.Any<CancellationToken>()).Returns(bigLinks);
        var tooMany = rig.Add(Schedule.Create(rig.World.Ws.Id, "ใหญ่", rig.World.Collection.Id, big.Id, ScheduleMode.Daily, ["18:00"], 6, "09:00", Today,
            "14:00", PostOrder.Rotate, "09:00", "21:00", 3, 0, 0, null, 420, Now)); // 150 links x 14 days
        var fine = rig.Add(rig.World.NewSchedule());

        var created = await rig.TopUp().EnsureAsync(rig.World.Ws.Id);

        Assert.Equal(14 * 2, created);
        Assert.Null(tooMany.GeneratedThrough); // tried again next time
        Assert.NotNull(fine.GeneratedThrough);
    }

    [Fact]
    public async Task Two_requests_filling_the_same_slots_do_not_double_the_posts_the_second_forgets_its_attempt_and_goes_again()
    {
        var rig = new Rig();
        rig.Add(rig.World.NewSchedule());
        rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(
            _ => throw new DuplicateKeyException("ix_posts_schedule_id_target_key_slot_key"), _ => Task.FromResult(1));

        var result = await rig.TopUp().GenerateAsync(rig.World.Ws.Id, rig.Active[0].Id);

        Assert.True(result.Created > 0);
        rig.Uow.Received(1).DiscardChanges();
        await rig.Uow.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        await rig.Media.DidNotReceive().RecordUseAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()); // this collection has no media
    }

    [Fact]
    public async Task When_the_other_request_wins_twice_the_run_gives_up_quietly()
    {
        var rig = new Rig();
        var (m1, _) = (Guid.NewGuid(), 0);
        rig.World.Posts.Clear();
        rig.World.AddPost("มีรูป", m1);
        rig.Add(rig.World.NewSchedule());
        rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new DuplicateKeyException("ix"));

        var result = await rig.TopUp().GenerateAsync(rig.World.Ws.Id, rig.Active[0].Id);

        Assert.Equal(0, result.Created);
        rig.Uow.Received(2).DiscardChanges();
        await rig.Media.DidNotReceive().RecordUseAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()); // nothing was saved: no use to count
    }

    [Fact]
    public async Task Generate_does_nothing_for_a_schedule_or_workspace_that_is_gone()
    {
        var rig = new Rig();
        Assert.Equal(0, (await rig.TopUp().GenerateAsync(rig.World.Ws.Id, Guid.NewGuid())).Created);
        Assert.Equal(0, (await rig.TopUp().GenerateAsync(Guid.NewGuid(), Guid.NewGuid())).Created);
    }
}
