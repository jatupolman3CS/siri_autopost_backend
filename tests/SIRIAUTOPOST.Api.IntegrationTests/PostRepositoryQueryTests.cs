using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The post queries the schedule engine, the claim rules and the reports are built on, against real PostgreSQL.
[Collection(ApiCollection.Name)]
public class PostRepositoryQueryTests(ApiFactory factory)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class World
    {
        public Guid Ws, Account, Device, Schedule, L1, L2, Cp1, Cp2, Cp3;
        public Dictionary<string, Post> P = new();
    }

    /// <summary>
    /// A workspace with a connected account and posts of one schedule to two links:
    /// P1 L1 cp1 +1h queued, P2 L1 cp2 +2h queued, P3 L2 cp1 +1h queued (P1 and P3 share a slot), P4 L1 cp3 -1h success,
    /// P5 L1 cp1 -2h failed, P6 L2 cp2 -3h success, P7 L1 cp2 -30h success, P8 L2 cp3 skipped, T a test post to L1 that succeeded now.
    /// </summary>
    private async Task<World> BuildAsync()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var w = new World { Ws = ws, Account = pair.AccountId, Device = pair.DeviceId, Schedule = Guid.NewGuid(), L1 = Guid.NewGuid(), L2 = Guid.NewGuid(), Cp1 = Guid.NewGuid(), Cp2 = Guid.NewGuid(), Cp3 = Guid.NewGuid() };
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == pair.AccountId);
            Post Make(string name, Guid link, Guid cp, double hours, string slot, string? done = null)
            {
                var at = Now.AddHours(hours);
                var p = Post.FromSchedule(ws, account, "g", "text " + name, [], at, Now, w.Schedule, cp, link, Post.LinkTargetKey(link), slot, null, null);
                switch (done)
                {
                    case "success": p.Claim(w.Device, at); p.CompletePosted(false, at); break;
                    case "failed": p.Claim(w.Device, at); p.Fail(FailureCode.Network, "x", at); break;
                    case "skipped": p.Skip(at, "x"); break;
                }
                db.Posts.Add(p);
                w.P[name] = p;
                return p;
            }
            Make("P1", w.L1, w.Cp1, 1, "A");
            Make("P2", w.L1, w.Cp2, 2, "B");
            Make("P3", w.L2, w.Cp1, 1, "A");
            Make("P4", w.L1, w.Cp3, -1, "C", "success");
            Make("P5", w.L1, w.Cp1, -2, "D", "failed");
            Make("P6", w.L2, w.Cp2, -3, "E", "success");
            Make("P7", w.L1, w.Cp2, -30, "F", "success");
            Make("P8", w.L2, w.Cp3, -4, "G", "skipped");
            var test = Post.Test(ws, account, "g", "test", [], Now, w.Cp3, w.L1, null, null);
            test.Claim(w.Device, Now);
            test.CompletePosted(false, Now);
            db.Posts.Add(test);
            w.P["T"] = test;
            await db.SaveChangesAsync();
        });
        return w;
    }

    private async Task<T> WithRepoAsync<T>(Func<IPostRepository, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IPostRepository>());
    }

    private static IEnumerable<Guid> Ids(World w, params string[] names) => names.Select(n => w.P[n].Id);

    [Fact]
    public async Task What_a_schedule_generated_can_be_found_again()
    {
        var w = await BuildAsync();

        var future = await WithRepoAsync(r => r.ListFutureQueuedByScheduleAsync(w.Schedule, Now));
        Assert.Equal(Ids(w, "P1", "P2", "P3").Order(), future.Select(p => p.Id).Order());

        var keys = await WithRepoAsync(r => r.ListScheduleKeysAsync(w.Schedule, Now.AddHours(-48)));
        Assert.Equal(8, keys.Count);
        Assert.Contains((Post.LinkTargetKey(w.L1), "A"), keys);
        Assert.Contains((Post.LinkTargetKey(w.L2), "A"), keys);
        // Only the posts due at or after the given time are listed.
        Assert.Equal(7, (await WithRepoAsync(r => r.ListScheduleKeysAsync(w.Schedule, Now.AddHours(-5)))).Count); // all but P7

        var counts = await WithRepoAsync(r => r.CountByScheduleAsync([w.Schedule, Guid.NewGuid()], Now.AddHours(-4.5), Now.AddHours(3)));
        Assert.Equal(7, counts[w.Schedule]); // P1..P6 and the skipped P8
        Assert.Single(counts);

        var next = await WithRepoAsync(r => r.NextQueuedAtByScheduleAsync([w.Schedule, Guid.NewGuid()], Now));
        Assert.Equal(w.P["P1"].ScheduledAt.ToUnixTimeMilliseconds(), next[w.Schedule].ToUnixTimeMilliseconds()); // the database keeps microseconds
        Assert.Single(next);
        Assert.Empty(await WithRepoAsync(r => r.NextQueuedAtByScheduleAsync([w.Schedule], Now.AddHours(5))));
    }

    [Fact]
    public async Task A_round_is_over_when_none_of_its_posts_is_open()
    {
        var w = await BuildAsync();

        var round = await WithRepoAsync(r => r.ListBySlotAsync(w.Schedule, "A"));
        Assert.Equal(Ids(w, "P1", "P3").Order(), round.Select(p => p.Id).Order());
        Assert.Equal(2, await WithRepoAsync(r => r.CountOpenInSlotAsync(w.Schedule, "A")));
        Assert.Equal(0, await WithRepoAsync(r => r.CountOpenInSlotAsync(w.Schedule, "C"))); // finished
        Assert.Equal(0, await WithRepoAsync(r => r.CountOpenInSlotAsync(w.Schedule, "G"))); // skipped
        Assert.Equal(0, await WithRepoAsync(r => r.CountOpenInSlotAsync(w.Schedule, "nothing")));

        await factory.WithDbAsync(async db =>
        {
            var p1 = await db.Posts.SingleAsync(p => p.Id == w.P["P1"].Id);
            p1.Claim(w.Device, Now);
            await db.SaveChangesAsync();
        });
        Assert.Equal(2, await WithRepoAsync(r => r.CountOpenInSlotAsync(w.Schedule, "A"))); // being posted is still open

        await factory.WithDbAsync(async db =>
        {
            (await db.Posts.SingleAsync(p => p.Id == w.P["P1"].Id)).CompletePosted(false, Now);
            (await db.Posts.SingleAsync(p => p.Id == w.P["P3"].Id)).Fail(FailureCode.Network, "x", Now);
            await db.SaveChangesAsync();
        });
        Assert.Equal(0, await WithRepoAsync(r => r.CountOpenInSlotAsync(w.Schedule, "A")));
    }

    [Fact]
    public async Task Posts_to_a_link_are_counted_for_its_cap_and_cooldown()
    {
        var w = await BuildAsync();

        var end = Now.AddHours(1);
        Assert.Equal(2, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L1, Now.AddHours(-24), end))); // P4 and the test post
        Assert.Equal(3, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L1, Now.AddHours(-48), end))); // and P7
        Assert.Equal(1, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L2, Now.AddHours(-24), end)));
        Assert.Equal(0, await WithRepoAsync(r => r.CountPublishedToLinkAsync(Guid.NewGuid(), Now.AddHours(-24), end)));
        // A day is [from, to): what went out later than `to` (the test post, now) or before `from` (P7) is not in it.
        Assert.Equal(1, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L1, Now.AddHours(-24), Now.AddMinutes(-5)))); // P4 alone
        Assert.Equal(1, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L1, Now.AddHours(-31), Now.AddHours(-29)))); // P7 alone

        var many = await WithRepoAsync(r => r.CountPublishedToLinksSinceAsync([w.L1, w.L2, Guid.NewGuid()], Now.AddHours(-24)));
        Assert.Equal((2, 1, 2), (many[w.L1], many[w.L2], many.Count));

        var last1 = await WithRepoAsync(r => r.LastPublishedToLinkAtAsync(w.L1));
        var last2 = await WithRepoAsync(r => r.LastPublishedToLinkAtAsync(w.L2));
        Assert.Equal(Now.ToUnixTimeSeconds(), last1!.Value.ToUnixTimeSeconds());
        Assert.Equal(Now.AddHours(-3).ToUnixTimeSeconds(), last2!.Value.ToUnixTimeSeconds());
        Assert.Null(await WithRepoAsync(r => r.LastPublishedToLinkAtAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task The_posts_most_recently_used_for_a_link_come_newest_first_and_skip_what_never_went_out()
    {
        var w = await BuildAsync();

        var two = await WithRepoAsync(r => r.ListRecentCollectionPostIdsByLinkAsync([w.L1, w.L2], 2));
        // L1, newest first: P2 (+2h, cp2), P1 (+1h, cp1), the test post (now, cp3), P4 (-1h, cp3)... failed P5 is not there.
        Assert.Equal([w.Cp2, w.Cp1], two[w.L1]);
        Assert.Equal([w.Cp1, w.Cp2], two[w.L2]); // P3 and P6; the skipped P8 never reached the group

        var all = await WithRepoAsync(r => r.ListRecentCollectionPostIdsByLinkAsync([w.L1], 10));
        Assert.Equal([w.Cp2, w.Cp1, w.Cp3, w.Cp3, w.Cp2], all[w.L1]); // P2, P1, T, P4, P7
        Assert.Empty(await WithRepoAsync(r => r.ListRecentCollectionPostIdsByLinkAsync([], 5)));
        Assert.Empty(await WithRepoAsync(r => r.ListRecentCollectionPostIdsByLinkAsync([w.L1], 0)));
        Assert.Empty(await WithRepoAsync(r => r.ListRecentCollectionPostIdsByLinkAsync([Guid.NewGuid()], 5)));
    }

    [Fact]
    public async Task The_engine_s_limits_and_failure_rate_count_real_posts()
    {
        var w = await BuildAsync();

        Assert.Equal(3, await WithRepoAsync(r => r.CountPublishedInWorkspaceSinceAsync(w.Ws, Now.AddHours(-24)))); // P4, P6 and the test post
        Assert.Equal(4, await WithRepoAsync(r => r.CountPublishedInWorkspaceSinceAsync(w.Ws, Now.AddHours(-48))));

        // Finished in the last day: P4 and P6 succeeded, P5 failed; the test post is left out of the rate.
        Assert.Equal((3, 1), await WithRepoAsync(r => r.CountOutcomesSinceAsync(w.Ws, Now.AddHours(-24))));
        Assert.Equal((4, 1), await WithRepoAsync(r => r.CountOutcomesSinceAsync(w.Ws, Now.AddHours(-48))));
        Assert.Equal((0, 0), await WithRepoAsync(r => r.CountOutcomesSinceAsync(Guid.NewGuid(), Now.AddHours(-48))));

        // Newest first: the test post (now), P4 (-1h), P5 (-2h), P6 (-3h).
        var outcomes = await WithRepoAsync(r => r.ListRecentOutcomesAsync(w.Account, 3));
        Assert.Equal([PostStatus.Success, PostStatus.Success, PostStatus.Failed], outcomes);
        Assert.Equal(5, (await WithRepoAsync(r => r.ListRecentOutcomesAsync(w.Account, 50))).Count);
    }

    [Fact]
    public async Task Reports_read_finished_real_posts_without_their_text()
    {
        var w = await BuildAsync();

        var rows = await WithRepoAsync(r => r.ListOutcomesAsync(w.Ws, Now.AddHours(-4), Now.AddHours(4)));
        Assert.Equal(Ids(w, "P4", "P5", "P6").Order(), rows.Select(x => x.Id).Order()); // no test, no skipped, no queued, no P7
        var p5 = rows.Single(x => x.Id == w.P["P5"].Id);
        Assert.Equal((PostStatus.Failed, w.L1, w.Cp1, w.Account), (p5.Status, p5.LinkId, p5.CollectionPostId, p5.AccountId));
        Assert.Null(rows.Single(x => x.Id == w.P["P4"].Id).TargetUrl);
        Assert.NotNull(rows.Single(x => x.Id == w.P["P4"].Id).PublishedAt);
        Assert.Null(p5.PublishedAt);

        var posted = await WithRepoAsync(r => r.CountPostedByCollectionPostAsync(w.Ws));
        Assert.Equal((2, 1), (posted[w.Cp2], posted[w.Cp3])); // P6 and P7; P4 (the test post is not counted)
        Assert.False(posted.ContainsKey(w.Cp1)); // never succeeded

        var hours = await WithRepoAsync(r => r.CountPublishedByHourAsync(w.Ws, Now.AddHours(-48), 0));
        Assert.Equal(3, hours.Values.Sum()); // P4, P6, P7; not the test post
        Assert.Equal(1, hours[Now.AddHours(-1).Hour]);
        Assert.Equal(1, hours[Now.AddHours(-3).Hour]);
        var thai = await WithRepoAsync(r => r.CountPublishedByHourAsync(w.Ws, Now.AddHours(-48), 420));
        Assert.Equal(1, thai[Now.AddHours(-1).ToOffset(TimeSpan.FromHours(7)).Hour]);
    }

    [Fact]
    public async Task A_post_being_posted_counts_for_the_group_on_the_day_it_was_taken()
    {
        var w = await BuildAsync();
        var posting = w.P["P1"]; // L1, still queued: the device takes it now
        await factory.WithDbAsync(async db =>
        {
            (await db.Posts.SingleAsync(p => p.Id == posting.Id)).Claim(w.Device, Now);
            await db.SaveChangesAsync();
        });

        Assert.Equal(3, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L1, Now.AddHours(-24), Now.AddHours(1)))); // P4, the test post and the one on its way
        Assert.Equal(1, await WithRepoAsync(r => r.CountPublishedToLinkAsync(w.L1, Now.AddHours(-24), Now.AddMinutes(-5)))); // taken now: not in an earlier day
    }

    [Fact]
    public async Task Only_posts_a_device_attempted_count_for_the_failure_rate_and_the_failure_streak()
    {
        var w = await BuildAsync(); // the last day: P4 and P6 succeeded, P5 failed, the test post is left out
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == w.Account);
            Post Settled(string name, Action<Post> settle)
            {
                var p = Post.FromSchedule(w.Ws, account, "g", name, [], Now.AddMinutes(-30), Now.AddHours(-1), w.Schedule, w.Cp1, w.L2, Post.LinkTargetKey(w.L2), name, null, null);
                settle(p);
                db.Posts.Add(p);
                return p;
            }
            Settled("ถึงเพดานรวม", p => p.Fail(FailureCode.Quota, "ครบโควตา", Now)); // refused before it was handed out
            Settled("ไม่พบกลุ่ม", p => p.Fail(FailureCode.Network, "ไม่พบลิงก์ของกลุ่ม", Now)); // the extension no longer has the group
            Settled("ที่เครื่องลองแล้ว", p => { p.Claim(w.Device, Now); p.Fail(FailureCode.Network, "หน้าเว็บค้าง", Now.AddMinutes(1)); });
            Settled("โควตาหลังรับงาน", p => { p.Claim(w.Device, Now); p.Fail(FailureCode.Quota, "x", Now); }); // not something a device reports, but never counted either
            await db.SaveChangesAsync();
        });

        // Only the one the device took and reported adds to the 3 finished and 1 failed it had.
        Assert.Equal((4, 2), await WithRepoAsync(r => r.CountOutcomesSinceAsync(w.Ws, Now.AddHours(-24))));
        var recent = await WithRepoAsync(r => r.ListRecentOutcomesAsync(w.Account, 50));
        Assert.Equal(6, recent.Count); // the 5 from before and the attempted one
        Assert.Equal(PostStatus.Failed, recent[0]);
    }

    [Fact]
    public async Task Removing_posts_goes_through_the_unit_of_work()
    {
        var w = await BuildAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var posts = scope.ServiceProvider.GetRequiredService<IPostRepository>();
            var future = await posts.ListFutureQueuedByScheduleAsync(w.Schedule, Now);
            posts.RemoveRange(future);
            // Nothing is deleted until the unit of work saves.
            Assert.Equal(3, (await WithRepoAsync(r => r.ListFutureQueuedByScheduleAsync(w.Schedule, Now))).Count);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        Assert.Empty(await WithRepoAsync(r => r.ListFutureQueuedByScheduleAsync(w.Schedule, Now)));
        Assert.Equal(5, (await WithRepoAsync(r => r.ListScheduleKeysAsync(w.Schedule, Now.AddHours(-48)))).Count); // finished history stays
    }
}
