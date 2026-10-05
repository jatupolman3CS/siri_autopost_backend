using Microsoft.Extensions.Logging.Abstractions;
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
        public TopUpThrottle Throttle { get; } = new();
        public List<Schedule> Active { get; } = [];

        // What the database holds of each schedule: a save writes it, discarding the unsaved changes reads it back.
        private readonly Dictionary<Guid, (DateOnly? Through, int Cursor)> saved = new();

        public Rig()
        {
            Workspaces.GetByIdAsync(World.Ws.Id, Arg.Any<CancellationToken>()).Returns(World.Ws);
            Schedules.ListActiveAsync(World.Ws.Id, Arg.Any<CancellationToken>()).Returns(_ => Active.Where(s => s.Active).ToList());
            Schedules.GetAsync(World.Ws.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(c => Active.FirstOrDefault(s => s.Id == c.ArgAt<Guid>(1)));
            Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                foreach (var s in Active) saved[s.Id] = (s.GeneratedThrough, s.Cursor);
                return Task.FromResult(1);
            });
            Uow.When(u => u.DiscardChanges()).Do(_ =>
            {
                foreach (var s in Active)
                {
                    var (through, cursor) = saved.GetValueOrDefault(s.Id);
                    if (through is { } day) s.MarkGenerated(day, cursor);
                    else s.ResetGenerated();
                }
            });
            // A transaction just runs its work (the real one commits it and holds the lock).
            Uow.ExecuteInTransactionAsync(Arg.Any<string?>(), Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>())
                .Returns(c => c.ArgAt<Func<Task>>(1)());
        }

        public ScheduleTopUp TopUp(DateTimeOffset? now = null) =>
            new(Workspaces, Schedules, World.Materializer(), Media, Uow, new FixedClock(now ?? Now), Throttle, NullLogger<ScheduleTopUp>.Instance);

        public Schedule Add(Schedule s)
        {
            Active.Add(s);
            saved[s.Id] = (s.GeneratedThrough, s.Cursor);
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
        rig.Uow.Received().DiscardChanges();
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

        Assert.Equal((0, false), (result.Created, result.Advanced));
        await rig.Uow.Received(ScheduleTopUp.Attempts).SaveChangesAsync(Arg.Any<CancellationToken>()); // three tries, then it leaves it to the other request
        await rig.Media.DidNotReceive().RecordUseAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()); // nothing was saved: no use to count
    }

    [Fact]
    public async Task Generate_does_nothing_for_a_schedule_or_workspace_that_is_gone()
    {
        var rig = new Rig();
        Assert.Equal(0, (await rig.TopUp().GenerateAsync(rig.World.Ws.Id, Guid.NewGuid())).Created);
        Assert.Equal(0, (await rig.TopUp().GenerateAsync(Guid.NewGuid(), Guid.NewGuid())).Created);
    }

    // ---- concurrency: the lock, the retries, the catch-all and the throttle ----

    [Fact]
    public async Task A_run_takes_the_lock_of_its_schedule_and_reads_again_after_it()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());

        await rig.TopUp().GenerateAsync(rig.World.Ws.Id, s.Id);

        await rig.Uow.Received(1).ExecuteInTransactionAsync($"schedule:{s.Id:N}", Arg.Any<Func<Task>>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            rig.Uow.DiscardChanges(); // what was read before the lock may be out of date
            rig.Schedules.GetAsync(rig.World.Ws.Id, s.Id, Arg.Any<CancellationToken>());
            rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(typeof(ConcurrencyConflictException))]
    [InlineData(typeof(DuplicateKeyException))]
    public async Task A_run_that_loses_the_race_twice_still_goes_a_third_time(Type loser)
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        var calls = 0;
        rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            ++calls < 3 ? throw (Exception)Activator.CreateInstance(loser, loser == typeof(DuplicateKeyException) ? "ix" : null)! : Task.FromResult(1));

        var result = await rig.TopUp().GenerateAsync(rig.World.Ws.Id, s.Id);

        Assert.Equal((3, true), (calls, result.Advanced));
        Assert.True(result.Created > 0);
    }

    [Fact]
    public async Task Inside_a_running_transaction_a_lost_race_is_left_to_the_outer_one_instead_of_retried()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        rig.Uow.InTransaction.Returns(true); // a failed save has left that transaction unusable: nothing can be retried inside it
        rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new ConcurrencyConflictException());

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => rig.TopUp().GenerateAsync(rig.World.Ws.Id, s.Id));

        await rig.Uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refusal_of_the_run_is_thrown_to_the_one_who_asked_for_it()
    {
        var rig = new Rig();
        rig.World.Links.Clear();
        rig.World.AddLink("long", new string('C', 100));
        var s = rig.Add(rig.World.NewSchedule());
        rig.World.Posts.Clear();
        rig.World.AddPost(new string('ก', 4800) + string.Concat(Enumerable.Repeat("{{code}}", 25))); // too long once the 100-character codes are in
        rig.World.SetLinks.ListBySetAsync(rig.World.Ws.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => rig.World.Links.ToList());

        await Assert.ThrowsAsync<DomainException>(() => rig.TopUp().GenerateAsync(rig.World.Ws.Id, s.Id));
    }

    [Fact]
    public async Task The_top_up_never_throws_whatever_goes_wrong_and_the_others_are_still_made()
    {
        var rig = new Rig();
        var poisoned = rig.Add(Schedule.Create(rig.World.Ws.Id, "เสีย", rig.World.Collection.Id, rig.World.Set.Id, ScheduleMode.Once, ["18:00"], 6, "09:00",
            Today.AddDays(1), "23:59", PostOrder.Rotate, "09:00", "21:00", 3, 0, 0, null, -300, Now));
        var fine = rig.Add(rig.World.NewSchedule());
        // Poisoned the way an old row can be: a date that overflows when turned into UTC.
        typeof(Schedule).GetProperty(nameof(Schedule.StartDate))!.SetValue(poisoned, DateOnly.MaxValue);

        var created = await rig.TopUp().EnsureAsync(rig.World.Ws.Id); // no exception

        Assert.Equal(14 * 2, created);
        Assert.NotNull(fine.GeneratedThrough);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("save")]
    public async Task Database_errors_do_not_escape_the_top_up(string where)
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule(ScheduleMode.Once, start: Today.AddDays(-1))); // switched off: needs a save
        if (where == "list") rig.Schedules.ListActiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        else rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id));

        rig.Uow.Received().DiscardChanges(); // the context is left clean for the claim that follows
        Assert.NotNull(s);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_swallowed()
    {
        var rig = new Rig();
        rig.Add(rig.World.NewSchedule());
        using var cts = new CancellationTokenSource();
        rig.Schedules.ListActiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.TopUp().EnsureAsync(rig.World.Ws.Id, cts.Token));
    }

    [Fact]
    public async Task A_schedule_that_could_not_be_made_is_left_alone_for_ten_minutes_then_tried_again()
    {
        var rig = new Rig();
        rig.World.Posts.Clear(); // nothing to post: the run cannot advance
        var s = rig.Add(rig.World.NewSchedule());
        var queries = () => rig.World.CollectionPosts.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICollectionPostRepository.ListByCollectionAsync));

        await rig.TopUp().EnsureAsync(rig.World.Ws.Id);
        Assert.Equal(1, queries());
        await rig.TopUp(Now.AddMinutes(5)).EnsureAsync(rig.World.Ws.Id); // claims keep coming: the schedule is not looked at again
        await rig.TopUp(Now.AddMinutes(9)).EnsureAsync(rig.World.Ws.Id);
        Assert.Equal(1, queries());

        rig.World.AddPost("มีโพสต์แล้ว");
        await rig.TopUp(Now.AddMinutes(11)).EnsureAsync(rig.World.Ws.Id); // the ten minutes are over
        Assert.Equal(2, queries());
        Assert.NotNull(s.GeneratedThrough);
    }

    [Fact]
    public async Task A_schedule_that_failed_is_left_alone_too_and_creating_or_resuming_it_does_not_wait_for_the_throttle()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        var fail = true;
        rig.Uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ => fail ? throw new InvalidOperationException("db down") : Task.FromResult(1));

        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id));
        Assert.True(rig.Throttle.IsDelayed(s.Id, Now.AddMinutes(1)));
        await rig.Uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(0, await rig.TopUp(Now.AddMinutes(1)).EnsureAsync(rig.World.Ws.Id)); // throttled: no new attempt
        await rig.Uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        fail = false;
        var made = await rig.TopUp(Now.AddMinutes(1)).GenerateAsync(rig.World.Ws.Id, s.Id); // what the handlers call: no throttle
        Assert.True(made.Created > 0);
        Assert.False(rig.Throttle.IsDelayed(s.Id, Now.AddMinutes(1))); // and a success lifts the hold
    }

    [Fact]
    public void The_throttle_holds_a_schedule_for_ten_minutes_and_forgets_it_after_that()
    {
        var throttle = new TopUpThrottle();
        var id = Guid.NewGuid();
        Assert.False(throttle.IsDelayed(id, Now));

        throttle.Hold(id, Now);

        Assert.True(throttle.IsDelayed(id, Now.AddMinutes(9)));
        Assert.False(throttle.IsDelayed(id, Now.AddMinutes(10)));
        Assert.False(throttle.IsDelayed(id, Now.AddMinutes(1))); // forgotten for good once it ran out
        throttle.Hold(id, Now);
        throttle.Clear(id);
        Assert.False(throttle.IsDelayed(id, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task A_full_queue_stops_the_top_up_of_that_schedule_quietly_until_there_is_room()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        rig.World.PostRepo.CountQueuedFutureAsync(rig.World.Ws.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(10_000);

        Assert.Equal(0, await rig.TopUp().EnsureAsync(rig.World.Ws.Id)); // no exception

        Assert.Empty(rig.World.Added);
        Assert.Null(s.GeneratedThrough);
        Assert.True(rig.Throttle.IsDelayed(s.Id, Now.AddMinutes(1)));

        rig.World.PostRepo.CountQueuedFutureAsync(rig.World.Ws.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
        Assert.Equal(14 * 2, await rig.TopUp(Now.AddMinutes(11)).EnsureAsync(rig.World.Ws.Id)); // room again, and the ten minutes are over
    }

    [Fact]
    public async Task Creating_or_resuming_a_schedule_with_a_full_queue_is_refused_with_the_reason()
    {
        var rig = new Rig();
        var s = rig.Add(rig.World.NewSchedule());
        rig.World.PostRepo.CountQueuedFutureAsync(rig.World.Ws.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(9_990);

        var ex = await Assert.ThrowsAsync<QueueFullException>(() => rig.TopUp().GenerateAsync(rig.World.Ws.Id, s.Id));

        Assert.Contains("9,990", ex.Message);
    }
}
