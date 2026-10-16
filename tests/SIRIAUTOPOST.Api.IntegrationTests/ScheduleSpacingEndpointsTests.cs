using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// 2026-10-13: "how many minutes apart per group" on a schedule (the plan, the claim) and handing a link set to another
// extension in the middle of the day (the timeline's "change extension").
[Collection(ApiCollection.Name)]
public class ScheduleSpacingEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private sealed record World(HttpClient Owner, Guid Ws, HttpClient Device, PairResultDto Pair, CollectionDto Collection, LinkSetDto Set);

    private async Task<World> WorldAsync(int groups, string plan = "pro")
    {
        var (owner, _, ws) = await factory.SignUpAsync(plan);
        var (device, pair) = await factory.PairDeviceAsync(owner, ws);
        var collection = await owner.CreateCollectionAsync(ws);
        await owner.AddPostAsync(ws, collection.Id);
        var set = await owner.CreateLinkSetAsync(ws, "ชุดทดสอบ", pair.AccountId);
        for (var i = 0; i < groups; i++) await owner.AddLinkAsync(ws, set.Id, $"https://www.facebook.com/groups/spread{i}x{Guid.NewGuid():N}"[..60]);
        return new World(owner, ws, device, pair, collection, set);
    }

    private static object ScheduleBody(World w, int? gap = null) => new
    {
        collectionId = w.Collection.Id, linkSetId = w.Set.Id, mode = "daily", times = new[] { "18:00" }, order = "rotate", utcOffsetMinutes = 0,
        startNow = true, groupGapMinutes = gap ?? 0,
    };

    private static async Task<ScheduleCreatedDto> CreateScheduleAsync(World w, int? gap = null)
    {
        var res = await w.Owner.PostAsJsonAsync($"/api/workspaces/{w.Ws}/schedules", ScheduleBody(w, gap), Json);
        return await res.ReadAsync<ScheduleCreatedDto>();
    }

    private async Task<List<PostDto>> PostsAsync(World w) =>
        (await w.Owner.GetFromJsonAsync<List<PostDto>>(
            $"/api/workspaces/{w.Ws}/posts?from={Uri.EscapeDataString(factory.Clock.GetUtcNow().AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(factory.Clock.GetUtcNow().AddDays(14).ToString("O"))}", Json))!;

    private static List<double> Gaps(IEnumerable<PostDto> posts)
    {
        var times = posts.Select(p => p.ScheduledAt).Order().ToList();
        return times.Zip(times.Skip(1), (a, b) => (b - a).TotalMinutes).ToList();
    }

    [Fact]
    public async Task A_schedule_with_a_gap_places_its_groups_that_many_minutes_apart_and_says_so()
    {
        var w = await WorldAsync(groups: 4);

        var created = await CreateScheduleAsync(w, gap: 12);

        Assert.Equal(12, created.Schedule.GroupGapMinutes);
        var posts = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).ToList();
        Assert.True(posts.Count >= 4);
        Assert.All(Gaps(posts), gap => Assert.True(gap >= 12, $"two posts only {gap} minutes apart")); // the rounds share one line, too
        var listed = (await w.Owner.GetFromJsonAsync<List<ScheduleDto>>($"/api/workspaces/{w.Ws}/schedules", Json))!;
        Assert.Equal(12, Assert.Single(listed).GroupGapMinutes);
    }

    [Fact]
    public async Task A_gap_outside_the_range_is_refused_when_a_schedule_is_made()
    {
        var w = await WorldAsync(groups: 2);

        var res = await w.Owner.PostAsJsonAsync($"/api/workspaces/{w.Ws}/schedules", ScheduleBody(w, gap: 500), Json);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_spacing_of_a_schedule_that_exists_can_be_set_and_lays_out_what_is_queued_without_making_it_again()
    {
        var w = await WorldAsync(groups: 5);
        var created = await CreateScheduleAsync(w); // the workspace's smart delay: 3 to 12 minutes, random
        var before = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).ToList();

        var res = await w.Owner.PutAsJsonAsync($"/api/workspaces/{w.Ws}/schedules/{created.Schedule.Id}/spacing", new { groupGapMinutes = 25 }, Json);

        Assert.Equal(25, (await res.ReadAsync<ScheduleDto>()).GroupGapMinutes);
        var after = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).ToList();
        Assert.Equal(before.Select(p => p.Id).Order(), after.Select(p => p.Id).Order()); // the same posts: nothing was made again
        Assert.All(Gaps(after), gap => Assert.True(gap >= 25, $"two posts only {gap} minutes apart"));

        // Back to the smart delay: allowed, and the posts stay.
        var off = await w.Owner.PutAsJsonAsync($"/api/workspaces/{w.Ws}/schedules/{created.Schedule.Id}/spacing", new { groupGapMinutes = 0 }, Json);
        Assert.Equal(0, (await off.ReadAsync<ScheduleDto>()).GroupGapMinutes);

        var bad = await w.Owner.PutAsJsonAsync($"/api/workspaces/{w.Ws}/schedules/{created.Schedule.Id}/spacing", new { groupGapMinutes = 121 }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_change_the_spacing()
    {
        var team = await factory.TeamAsync();
        var (_, pair) = await factory.PairDeviceAsync(team.Owner, team.Ws);
        var collection = await team.Owner.CreateCollectionAsync(team.Ws);
        await team.Owner.AddPostAsync(team.Ws, collection.Id);
        var set = await team.Owner.CreateLinkSetAsync(team.Ws, "ชุด", pair.AccountId);
        await team.Owner.AddLinkAsync(team.Ws, set.Id, $"https://www.facebook.com/groups/v{Guid.NewGuid():N}"[..50]);
        var w = new World(team.Owner, team.Ws, null!, pair, collection, set);
        var created = await CreateScheduleAsync(w);

        var res = await team.Viewer.PutAsJsonAsync($"/api/workspaces/{team.Ws}/schedules/{created.Schedule.Id}/spacing", new { groupGapMinutes = 10 }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task The_claim_holds_a_post_of_a_schedule_until_the_gap_has_passed_since_the_browser_took_the_last_one()
    {
        var w = await WorldAsync(groups: 2);
        var created = await CreateScheduleAsync(w, gap: 30);
        var posts = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).OrderBy(p => p.ScheduledAt).ToList();
        var (first, second) = (posts[0], posts[1]);
        Assert.True(second.ScheduledAt - first.ScheduledAt >= TimeSpan.FromMinutes(30));

        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            var claim = await w.Device.PostAsync("/api/device/jobs/claim", null);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            var job = (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(first.Id, job.PostId);
            (await w.Device.PostAsJsonAsync($"/api/device/jobs/{job.PostId}/result", new { ok = true })).EnsureSuccessStatusCode();
        }

        // A backlog: the second post is due now (a browser that was off, two rounds on top of each other). The anti-ban gap
        // (3 minutes) has long passed, the schedule's 30 minutes have not.
        using (factory.Clock.Advance(TimeSpan.FromMinutes(10)))
        {
            await factory.WithDbAsync(async db =>
            {
                var row = await db.Posts.SingleAsync(x => x.Id == second.Id);
                row.Reschedule(factory.Clock.GetUtcNow(), factory.Clock.GetUtcNow());
                await db.SaveChangesAsync();
            });
            Assert.Equal(HttpStatusCode.NoContent, (await w.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
            Assert.Equal(PostStatus.Queued, (await PostsAsync(w)).Single(p => p.Id == second.Id).Status); // held, not skipped or failed

            using (factory.Clock.Advance(TimeSpan.FromMinutes(25)))
            {
                var claim = await w.Device.PostAsync("/api/device/jobs/claim", null);
                Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
                Assert.Equal(second.Id, (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!.PostId);
            }
        }
    }

    [Fact]
    public async Task A_schedule_without_a_gap_is_not_held_by_the_claim()
    {
        var w = await WorldAsync(groups: 2);
        var created = await CreateScheduleAsync(w);
        var posts = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).OrderBy(p => p.ScheduledAt).ToList();

        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            var job = (await (await w.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            (await w.Device.PostAsJsonAsync($"/api/device/jobs/{job.PostId}/result", new { ok = true })).EnsureSuccessStatusCode();
        }
        using (factory.Clock.Advance(TimeSpan.FromHours(1))) // past the anti-ban gap and the second post's time
        {
            var claim = await w.Device.PostAsync("/api/device/jobs/claim", null);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            Assert.Equal(posts[1].Id, (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!.PostId);
        }
    }

    // ---- change extension in the middle of the day ----

    [Fact]
    public async Task Handing_a_set_to_another_extension_moves_the_waiting_posts_with_their_times_and_the_new_browser_posts_them()
    {
        var w = await WorldAsync(groups: 3);
        var (device2, second) = await factory.PairDeviceAsync(w.Owner, w.Ws);
        var created = await CreateScheduleAsync(w);
        var before = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).OrderBy(p => p.ScheduledAt).ToList();
        Assert.All(before, p => Assert.Equal(w.Pair.AccountId, p.AccountId));

        var res = await w.Owner.PostAsJsonAsync($"/api/workspaces/{w.Ws}/link-sets/{w.Set.Id}/handover", new { accountId = second.AccountId }, Json);

        var done = await res.ReadAsync<HandOverResultDto>();
        Assert.Equal(before.Count, done.Moved);
        Assert.Equal(second.AccountId, done.Set.PostAsAccountId);
        var after = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).OrderBy(p => p.ScheduledAt).ToList();
        Assert.All(after, p => Assert.Equal(second.AccountId, p.AccountId));
        Assert.Equal(before.Select(p => (p.Id, p.ScheduledAt, p.Content)), after.Select(p => (p.Id, p.ScheduledAt, p.Content))); // same plan

        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await w.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode); // the old browser has nothing
            var claim = await device2.PostAsync("/api/device/jobs/claim", null);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            Assert.Equal(after[0].Id, (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!.PostId);
        }
    }

    [Fact]
    public async Task Posts_that_already_went_out_or_failed_stay_with_the_old_extension_and_a_retry_follows_the_set()
    {
        var w = await WorldAsync(groups: 3);
        var (device2, second) = await factory.PairDeviceAsync(w.Owner, w.Ws);
        var created = await CreateScheduleAsync(w);
        Guid sent, failed;
        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            var job = (await (await w.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            sent = job.PostId;
            (await w.Device.PostAsJsonAsync($"/api/device/jobs/{sent}/result", new { ok = true })).EnsureSuccessStatusCode();
        }
        using (factory.Clock.Advance(TimeSpan.FromHours(2)))
        {
            var job = (await (await w.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            failed = job.PostId;
            (await w.Device.PostAsJsonAsync($"/api/device/jobs/{failed}/result", new { ok = false, error = "ล้มเหลว" })).EnsureSuccessStatusCode();
        }

        var waiting = (await PostsAsync(w)).Count(p => p.ScheduleId == created.Schedule.Id && p.Status == PostStatus.Queued);
        Assert.True(waiting > 1); // the rest of the round and the next two weeks

        var res = await w.Owner.PostAsJsonAsync($"/api/workspaces/{w.Ws}/link-sets/{w.Set.Id}/handover", new { accountId = second.AccountId }, Json);

        Assert.Equal(waiting, (await res.ReadAsync<HandOverResultDto>()).Moved); // only what was still waiting
        var posts = (await PostsAsync(w)).Where(p => p.ScheduleId == created.Schedule.Id).ToDictionary(p => p.Id);
        Assert.All(posts.Values.Where(p => p.Status == PostStatus.Queued), p => Assert.Equal(second.AccountId, p.AccountId));
        Assert.Equal((PostStatus.Success, w.Pair.AccountId), (posts[sent].Status, posts[sent].AccountId));
        Assert.Equal((PostStatus.Failed, w.Pair.AccountId), (posts[failed].Status, posts[failed].AccountId));

        // Post now on the red one: it goes to the extension that has the set today, not to the old one that would skip it.
        var run = await w.Owner.PostAsync($"/api/workspaces/{w.Ws}/posts/{failed}/run-now", null);
        Assert.Equal(second.AccountId, (await run.ReadAsync<PostDto>()).AccountId);
        var claim = await device2.PostAsync("/api/device/jobs/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(failed, (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!.PostId);
    }

    [Fact]
    public async Task Handing_over_is_refused_for_the_extension_that_has_the_set_an_unpaired_one_and_an_unknown_one()
    {
        var w = await WorldAsync(groups: 2);
        var url = $"/api/workspaces/{w.Ws}/link-sets/{w.Set.Id}/handover";

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Owner.PostAsJsonAsync(url, new { accountId = w.Pair.AccountId }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PostAsJsonAsync(url, new { accountId = Guid.NewGuid() }, Json)).StatusCode);
        var unpaired = await factory.SeedAccountAsync(w.Ws, "Facebook · Old PC");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await w.Owner.PostAsJsonAsync(url, new { accountId = unpaired.Id }, Json)).StatusCode);
    }

    [Fact]
    public async Task Handing_over_is_refused_while_a_group_of_the_set_belongs_to_a_third_extension()
    {
        var w = await WorldAsync(groups: 1);
        var (_, second) = await factory.PairDeviceAsync(w.Owner, w.Ws);
        var (_, third) = await factory.PairDeviceAsync(w.Owner, w.Ws);
        var held = (await w.Owner.LinkSetsAsync(w.Ws)).Single(s => s.Id == w.Set.Id).Links.Single().Url;
        var thirdsSet = await w.Owner.CreateLinkSetAsync(w.Ws, "ของเครื่องที่สาม", third.AccountId);
        // The group is in the third extension's set too (as data from before the rule had it; the API would refuse it now).
        await factory.WithDbAsync(async db =>
        {
            db.SetLinks.Add(SIRIAUTOPOST.Domain.Entities.SetLink.Create(w.Ws, thirdsSet.Id, null, held, null, 0, DateTimeOffset.UtcNow.AddDays(1)));
            await db.SaveChangesAsync();
        });

        var res = await w.Owner.PostAsJsonAsync($"/api/workspaces/{w.Ws}/link-sets/{w.Set.Id}/handover", new { accountId = second.AccountId }, Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal(w.Pair.AccountId, (await w.Owner.LinkSetsAsync(w.Ws)).Single(s => s.Id == w.Set.Id).PostAsAccountId); // nothing changed
    }

    [Fact]
    public async Task A_viewer_cannot_hand_a_set_over()
    {
        var team = await factory.TeamAsync();
        var (_, first) = await factory.PairDeviceAsync(team.Owner, team.Ws);
        var (_, second) = await factory.PairDeviceAsync(team.Owner, team.Ws);
        var set = await team.Owner.CreateLinkSetAsync(team.Ws, "ชุด", first.AccountId);

        var res = await team.Viewer.PostAsJsonAsync($"/api/workspaces/{team.Ws}/link-sets/{set.Id}/handover", new { accountId = second.AccountId }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
