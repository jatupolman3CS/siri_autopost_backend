using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Infrastructure.Data;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Requests that race: claims that top the same schedule up together, claims of one device that overlap, two writers of one post.
[Collection(ApiCollection.Name)]
public class EngineConcurrencyTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private const int Bangkok = 420;

    /// <summary>A workspace with a schedule that has a lot of posts to make: many groups, two slots a day.</summary>
    private async Task<(Shop Shop, ScheduleCreatedDto Created)> BigShopAsync(int groups)
    {
        var shop = await factory.ShopAsync(links: 1, posts: 3);
        var lines = string.Join('\n', Enumerable.Range(0, groups - 1).Select(i => $"https://www.facebook.com/groups/big{i}-{Guid.NewGuid():N}"[..44] + $" | G{i}"));
        (await shop.Owner.PostAsJsonAsync($"{shop.Api}/link-sets/{shop.Set.Id}/links/bulk", new { text = lines }, Json)).EnsureSuccessStatusCode();
        await shop.RefreshSetAsync();
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["09:00", "19:00"], Offset: Bangkok, Order: "rotate"));
        return (shop, created);
    }

    [Fact]
    public async Task Many_claims_that_arrive_together_after_a_long_silence_top_up_once_and_nobody_gets_an_error()
    {
        var (shop, created) = await BigShopAsync(60);
        using var _ = shop;
        var perDay = 60 * 2;

        // Three rounds: every time the browser has been away for ten days and then ten claims arrive at once, each of them
        // wanting to make the same ten days of posts (1,200 of them). Before the schedule was locked, these deadlocked.
        for (var round = 1; round <= 3; round++)
        {
            shop.Wait(TimeSpan.FromDays(10));
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => shop.Device.PostAsync("/api/device/jobs/claim", null)));

            Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"round {round}: {r.StatusCode}"));
            var all = await shop.PostsAsync(created.Schedule.Id);
            var future = all.Where(p => p.ScheduledAt > shop.Now).ToList();
            // Exactly the fortnight ahead, once: no slot twice.
            Assert.Equal(future.Count, future.Select(p => (p.LinkId, p.ScheduledAt.ToOffset(TimeSpan.FromMinutes(Bangkok)).ToString("yyyy-MM-ddTHH:mm"))).Distinct().Count());
            // The targets of a slot follow each other minutes apart, so a slot that began before now (the evening's, when the test
            // runs after midnight Bangkok time) can still have posts to come: allow one slot's worth on top of the fortnight.
            Assert.InRange(future.Count, 12 * perDay, 14 * perDay + 60);
        }
    }

    [Fact]
    public async Task A_schedule_row_the_engine_cannot_work_with_never_fails_a_claim_and_the_other_schedules_are_still_topped_up()
    {
        using var shop = await factory.ShopAsync(links: 2);
        // Made first, so the top-up meets it first. Then it is damaged by hand: the API refuses such a start date, but
        // rows from before the check (or from a bug) can be anything, and their dates overflow when turned into UTC.
        var broken = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], Offset: Bangkok, Name: "เสีย"));
        var fine = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], Offset: Bangkok, Name: "ปกติ"));
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SCHEDULES" SET mode = 'Once', start_date = '9999-12-31', once_time = '23:59', utc_offset_minutes = -300,
                generated_through = NULL WHERE id = {broken.Schedule.Id}
            """));
        shop.Wait(TimeSpan.FromDays(2));

        var claims = new[] { await shop.Device.PostAsync("/api/device/jobs/claim", null), await shop.Device.PostAsync("/api/device/jobs/claim", null) };

        Assert.All(claims, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, r.StatusCode.ToString()));
        var posts = await shop.PostsAsync(fine.Schedule.Id);
        Assert.True(posts.Max(p => p.ScheduledAt) > fine.LastAt, "the healthy schedule was topped up");
    }

    // ---- one device, one post at a time ----

    [Fact]
    public async Task Claims_of_one_device_that_overlap_hand_out_one_post_and_only_one()
    {
        using var shop = await factory.ShopAsync(links: 2);

        for (var round = 1; round <= 4; round++)
        {
            await shop.TestPostAsync(round % 2);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shop.Device.PostAsync("/api/device/jobs/claim", null)));

            Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"round {round}: {r.StatusCode}"));
            var jobs = results.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
            Assert.True(jobs.Count == 1, $"round {round}: {jobs.Count} claims were handed the post");
            Assert.Single(await shop.PostsAsync(), p => p.Status == PostStatus.Posting);
            await shop.ReportAsync((await jobs[0].ReadAsync<JobDto>()).PostId);
            shop.Wait(TimeSpan.FromMinutes(6)); // the anti-ban gap
        }
    }

    [Fact]
    public async Task A_post_two_requests_changed_at_once_is_refused_to_the_second_with_a_conflict()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var post = await shop.TestPostAsync(0);
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var (a, b) = (first.ServiceProvider.GetRequiredService<AppDbContext>(), second.ServiceProvider.GetRequiredService<AppDbContext>());
        var (one, other) = (await a.Posts.SingleAsync(p => p.Id == post.Id), await b.Posts.SingleAsync(p => p.Id == post.Id));

        one.Claim(shop.Pair.DeviceId, shop.Now);
        other.Claim(shop.Pair.DeviceId, shop.Now);
        await a.SaveChangesAsync();

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => b.SaveChangesAsync());
        Assert.Equal(PostStatus.Posting, (await shop.PostAsync(post.Id)).Status); // the first one's change stands, once
    }

    [Fact]
    public async Task Deleting_a_post_the_device_has_just_taken_is_a_conflict_not_an_error()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var post = await shop.TestPostAsync(0);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stale = await db.Posts.SingleAsync(p => p.Id == post.Id); // read by the web request...
        await shop.ClaimAsync(); // ...the device takes it...

        db.Posts.Remove(stale); // ...and the request deletes what it read

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.SaveChangesAsync());
    }
}
