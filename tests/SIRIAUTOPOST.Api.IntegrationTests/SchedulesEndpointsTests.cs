using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Schedules through the real API. The API's clock is the system clock plus an offset, so a time is chosen relative to now
// (two hours ahead) and the expected counts are worked out from where that time falls today.
[Collection(ApiCollection.Name)]
public class SchedulesEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private const int Bangkok = 420;

    private static async Task<string> TitleAsync(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title!;

    private static string LocalDay(DateTimeOffset at) => ToLocalDay(at, Bangkok);

    private static ScheduleSpec Daily(string slot, int offset = Bangkok, string order = "rotate", string? name = null) =>
        new(Times: [slot], Order: order, Offset: offset, Name: name);

    // ---- creating ----

    [Fact]
    public async Task A_workspace_starts_without_schedules()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        Assert.Empty((await client.GetFromJsonAsync<List<ScheduleDto>>($"/api/workspaces/{ws}/schedules", Json))!);
    }

    [Fact]
    public async Task A_daily_schedule_queues_a_fortnight_of_composed_posts_for_every_group()
    {
        using var shop = await factory.ShopAsync(links: 3, posts: 2);
        var (slot, ahead) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot, name: "ตารางประจำวัน"));

        var s = created.Schedule;
        Assert.Equal((ahead ? 14 : 13) * 3, created.Created);
        var posts = await shop.PostsAsync(s.Id);
        Assert.Equal(created.Created, posts.Count);
        Assert.Equal(posts.Min(p => p.ScheduledAt), created.FirstAt);
        Assert.Equal(posts.Max(p => p.ScheduledAt), created.LastAt);
        Assert.All(posts, p => Assert.True(p.ScheduledAt > shop.Now));

        // The schedule as the web app sees it.
        Assert.Equal(("ตารางประจำวัน", ScheduleMode.Daily, true, PostOrder.Rotate), (s.Name, s.Mode, s.Active, s.Order));
        Assert.Equal((shop.Collection.Id, shop.Set.Id, Bangkok), (s.CollectionId, s.LinkSetId, s.UtcOffsetMinutes));
        Assert.Equal([slot], s.Slots);
        Assert.Equal([slot], s.Times);
        Assert.Equal((3, 3, 2), (s.TargetCount, s.PerDay, s.UsablePosts));
        Assert.Equal(posts.Count(p => LocalDay(p.ScheduledAt) == LocalDay(shop.Now)), s.TodayCount);
        Assert.Equal(created.FirstAt, s.NextRunAt);

        // Each post is for one group: its code first, its address, a collection post, the browser's account.
        Assert.All(posts, p =>
        {
            Assert.Equal((PostStatus.Queued, Platform.Fb, false, shop.Pair.AccountId), (p.Status, p.Platform, p.IsTest, p.AccountId));
            var link = shop.Set.Links.Single(l => l.Id == p.LinkId);
            Assert.Equal((link.Url, link.Code, link.Name), (p.TargetUrl, p.Code, p.Target));
            Assert.Equal($"{link.Code}\n{shop.Posts.Single(c => c.Id == p.CollectionPostId).Text}", p.Content);
        });
        // Rotate: everyone in a slot gets the same post and the collection's posts go round in order.
        var perSlot = posts.GroupBy(p => LocalDay(p.ScheduledAt)).OrderBy(g => g.Key).Select(g => g.Select(p => p.CollectionPostId!.Value).Distinct().Single()).ToList();
        var order = shop.Posts.Select(p => p.Id).ToList();
        Assert.Equal(Enumerable.Range(0, perSlot.Count).Select(i => order[i % 2]), perSlot);

        var listed = Assert.Single(await shop.SchedulesAsync());
        Assert.Equal(s.Id, listed.Id);
        Assert.Equal(s.TodayCount, listed.TodayCount);
    }

    [Fact]
    public async Task A_schedule_with_no_name_is_called_after_what_it_pairs()
    {
        using var shop = await factory.ShopAsync();
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot));

        Assert.Equal("โปรโมชัน → กลุ่มขายของ", created.Schedule.Name);
    }

    [Fact]
    public async Task The_shuffle_order_is_kept_and_posts_vary_by_group()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 4);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot, order: "shuffle"));

        Assert.Equal(PostOrder.Shuffle, created.Schedule.Order);
        var posts = await shop.PostsAsync(created.Schedule.Id);
        foreach (var link in shop.Set.Links)
        {
            var seq = posts.Where(p => p.LinkId == link.Id).OrderBy(p => p.ScheduledAt).Select(p => p.CollectionPostId).ToList();
            for (var i = 1; i < seq.Count; i++) Assert.NotEqual(seq[i - 1], seq[i]);
        }
        Assert.True(posts.Select(p => p.CollectionPostId).Distinct().Count() > 1);
    }

    [Fact]
    public async Task A_target_with_its_own_times_posts_at_them_and_times_for_unknown_targets_are_dropped()
    {
        using var shop = await factory.ShopAsync(links: 3);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var (own, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(5));
        var ownKey = shop.Link(1).Id.ToString("N");
        var spec = Daily(slot) with
        {
            Overrides = new() { [ownKey] = [own], [Guid.NewGuid().ToString("N")] = ["07:00"], [shop.Link(2).Id.ToString("N")] = [] },
        };

        var created = await shop.CreateScheduleAsync(spec);

        var s = created.Schedule;
        Assert.Equal([ownKey], s.Overrides.Keys); // unknown targets and empty lists (= follow the schedule) are not kept
        Assert.Equal([own], s.Overrides[ownKey]);
        Assert.Equal((3, 3), (s.TargetCount, s.PerDay));
        var posts = await shop.PostsAsync(s.Id);
        var times = posts.GroupBy(p => p.LinkId).ToDictionary(g => g.Key!.Value, g => g.Select(p => p.ScheduledAt.ToOffset(TimeSpan.FromMinutes(Bangkok)).ToString("HH:mm")).Distinct().ToList());
        Assert.Equal([own], times[shop.Link(1).Id]);
        Assert.Equal(slot, times[shop.Link(0).Id].Min()); // the first in the slot is at the slot, the rest follow it
    }

    [Fact]
    public async Task Other_accounts_of_the_set_post_to_their_default_target()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var ig = await factory.SeedAccountAsync(shop.Ws);
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/link-sets/{shop.Set.Id}",
            new { name = shop.Set.Name, postAsAccountId = shop.Pair.AccountId, accountIds = new[] { ig.Id } }, Json)).EnsureSuccessStatusCode();
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot));

        Assert.Equal((2, 2), (created.Schedule.TargetCount, created.Schedule.PerDay));
        var other = (await shop.PostsAsync(created.Schedule.Id)).Where(p => p.AccountId == ig.Id).ToList();
        Assert.NotEmpty(other);
        Assert.All(other, p => Assert.Equal((ig.DefaultTarget, Platform.Fb, null, null, null), (p.Target, p.Platform, p.TargetUrl, p.Code, p.LinkId)));
        Assert.All(other, p => Assert.Equal(shop.Posts.Single(c => c.Id == p.CollectionPostId).Text, p.Content));
    }

    [Fact]
    public async Task Once_posts_on_its_day_only()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var tomorrow = LocalDay(shop.Now.AddDays(1));

        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: tomorrow, OnceTime: "14:00", Offset: Bangkok));

        Assert.Equal(2, created.Created);
        Assert.Equal(["14:00"], created.Schedule.Slots);
        Assert.Equal((tomorrow, "14:00"), (created.Schedule.StartDate, created.Schedule.OnceTime));
        Assert.All(await shop.PostsAsync(created.Schedule.Id), p => Assert.Equal(tomorrow, LocalDay(p.ScheduledAt)));
        Assert.Equal(0, created.Schedule.TodayCount);
    }

    [Fact]
    public async Task Start_now_queues_a_round_that_goes_out_one_group_at_a_time_and_only_once()
    {
        using var shop = await factory.ShopAsync(links: 3);

        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartNow: true, OnceTime: "03:00", Offset: Bangkok));

        Assert.True(created.Schedule.StartNow);
        Assert.Equal(LocalDay(shop.Now), created.Schedule.StartDate); // today, not the date or time that was sent
        Assert.Equal(3, created.Created);
        var posts = (await shop.PostsAsync(created.Schedule.Id)).OrderBy(p => p.ScheduledAt).ToList();
        Assert.Equal(3, posts.Count);
        Assert.InRange((posts[0].ScheduledAt - shop.Now).TotalSeconds, 1, 25); // the first group starts a moment after the click
        Assert.All(posts.Zip(posts.Skip(1)), pair => Assert.True(pair.Second.ScheduledAt - pair.First.ScheduledAt >= TimeSpan.FromMinutes(1)));
        Assert.Equal(3, posts.Select(p => p.LinkId).Distinct().Count());

        // Pausing and resuming does not start another opening round.
        Assert.True((await shop.SetActiveAsync(created.Schedule.Id, false)).IsSuccessStatusCode);
        Assert.True((await shop.SetActiveAsync(created.Schedule.Id, true)).IsSuccessStatusCode);
        Assert.DoesNotContain((await shop.PostsAsync(created.Schedule.Id)), p => p.ScheduledAt < shop.Now.AddSeconds(1));
    }

    [Fact]
    public async Task A_time_that_is_over_makes_nothing_but_the_schedule_is_made()
    {
        using var shop = await factory.ShopAsync();

        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: LocalDay(shop.Now), OnceTime: "00:00", Offset: Bangkok));

        Assert.Equal((0, null, null), (created.Created, created.FirstAt, created.LastAt));
        Assert.Null(created.Schedule.NextRunAt);
        Assert.Empty(await shop.PostsAsync(created.Schedule.Id));
    }

    [Fact]
    public async Task A_schedule_that_starts_later_makes_posts_from_its_start()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var start = LocalDay(shop.Now.AddDays(3));

        var created = await shop.CreateScheduleAsync(Daily(slot) with { StartDate = start });

        Assert.Equal(11 * 2, created.Created); // day 3 to day 13
        Assert.All(await shop.PostsAsync(created.Schedule.Id), p => Assert.True(string.CompareOrdinal(LocalDay(p.ScheduledAt), start) >= 0));
    }

    [Fact]
    public async Task A_group_listed_twice_in_another_case_gets_one_post_per_slot()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await shop.Owner.AddLinkAsync(shop.Ws, shop.Set.Id, "https://www.facebook.com/groups/CaseGroup", "C1");
        await shop.Owner.AddLinkAsync(shop.Ws, shop.Set.Id, "https://www.facebook.com/groups/casegroup", "C2"); // the same group
        await shop.Owner.AddLinkAsync(shop.Ws, shop.Set.Id, "https://www.facebook.com/groups/CASEGROUP", "C3"); // and again
        var (slot, ahead) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot));

        Assert.Equal(2, created.Schedule.TargetCount); // the first link and the first spelling of CaseGroup
        Assert.Equal((ahead ? 14 : 13) * 2, created.Created);
        var posts = await shop.PostsAsync(created.Schedule.Id);
        Assert.Equal(["C0", "C1"], posts.Select(p => p.Code).Distinct().Order()); // the shop's own link and the first spelling
        Assert.DoesNotContain(posts, p => p.Code is "C2" or "C3");
    }

    [Theory]
    [InlineData("weekdays")]
    [InlineData("weekend")]
    public async Task Weekdays_and_weekend_follow_the_local_calendar(string mode)
    {
        using var shop = await factory.ShopAsync(links: 1);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot) with { Mode = mode });

        var posts = await shop.PostsAsync(created.Schedule.Id);
        Assert.NotEmpty(posts);
        DayOfWeek[] allowed = mode == "weekdays"
            ? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
            : [DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday]; // the design's "ศุกร์–อาทิตย์"
        Assert.All(posts, p => Assert.Contains(p.ScheduledAt.ToOffset(TimeSpan.FromMinutes(Bangkok)).DayOfWeek, allowed));
        // Every matching day of the fortnight is there (today's only when its time is still ahead).
        var today = DateOnly.FromDateTime(shop.Now.ToOffset(TimeSpan.FromMinutes(Bangkok)).DateTime);
        var matching = Enumerable.Range(0, 14).Select(today.AddDays).Count(d => allowed.Contains(d.DayOfWeek));
        Assert.InRange(posts.Count, matching - 1, matching);
    }

    [Fact]
    public async Task Interval_and_drip_work_out_their_slots()
    {
        using var shop = await factory.ShopAsync(links: 2);

        var interval = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "interval", EveryHours: 8, FirstTime: "07:00", Offset: Bangkok));
        Assert.Equal(["07:00", "15:00", "23:00"], interval.Schedule.Slots);
        Assert.Equal((2, 6), (interval.Schedule.TargetCount, interval.Schedule.PerDay));

        var drip = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "drip", DripFrom: "10:00", DripTo: "22:00", DripCount: 4, Offset: Bangkok));
        Assert.Equal(["10:00", "14:00", "18:00", "22:00"], drip.Schedule.Slots);
        Assert.Equal(8, drip.Schedule.PerDay);
    }

    [Fact]
    public async Task The_bump_and_auto_delete_settings_come_back()
    {
        using var shop = await factory.ShopAsync("agency");
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(Daily(slot) with { BumpHours = 12, AutoDeleteDays = 7 });

        Assert.Equal((12, 7), (created.Schedule.BumpHours, created.Schedule.AutoDeleteDays));
    }

    [Fact]
    public async Task Bumping_is_a_top_plan_feature()
    {
        using var shop = await factory.ShopAsync(); // Pro
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var refused = await shop.TryCreateScheduleAsync(Daily(slot) with { BumpHours = 2 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Premium", await TitleAsync(refused));

        // No bump asked: no plan check.
        Assert.Equal(HttpStatusCode.OK, (await shop.TryCreateScheduleAsync(Daily(slot) with { BumpHours = 0 })).StatusCode);
    }

    // ---- refusals ----

    [Fact]
    public async Task A_collection_without_posts_cannot_be_scheduled_and_neither_can_one_with_nothing_approved()
    {
        using var shop = await factory.ShopAsync(posts: 0);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        var empty = await shop.TryCreateScheduleAsync(Daily(slot));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Contains("ยังไม่มีโพสต์", await TitleAsync(empty));

        // With approval on, a draft is not usable.
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}",
            new { name = "โปรโมชัน", description = "", icon = (string?)null, settings = WorkflowTestSupport.Settings(requireApproval: true) }, Json)).EnsureSuccessStatusCode();
        var draft = await shop.Owner.AddPostAsync(shop.Ws, shop.Collection.Id, "แบบร่าง");
        var notApproved = await shop.TryCreateScheduleAsync(Daily(slot));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notApproved.StatusCode);
        Assert.Contains("อนุมัติ", await TitleAsync(notApproved));

        foreach (var action in new[] { "request", "approve" })
            (await shop.Owner.PostAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}/posts/{draft.Id}/approval", new { action }, Json)).EnsureSuccessStatusCode();
        var created = await shop.CreateScheduleAsync(Daily(slot));
        Assert.Equal(1, created.Schedule.UsablePosts);
    }

    [Fact]
    public async Task Without_a_connected_facebook_account_there_is_nobody_to_post()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var collection = await client.CreateCollectionAsync(ws);
        await client.AddPostAsync(ws, collection.Id);
        var set = await client.CreateLinkSetAsync(ws);
        await client.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/plants");
        var body = new { collectionId = collection.Id, linkSetId = set.Id, mode = "daily", times = new[] { "10:00" }, order = "rotate", utcOffsetMinutes = Bangkok };

        var noDevice = await client.PostAsJsonAsync($"/api/workspaces/{ws}/schedules", body, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDevice.StatusCode);
        Assert.Contains("ผูกเครื่อง", await TitleAsync(noDevice));

        // A set that names an account that is not connected (the demo page) is no better.
        var demoPage = await factory.SeedAccountAsync(ws, "เพจ", "เพจ");
        var named = await client.CreateLinkSetAsync(ws, "ชื่อบัญชี", demoPage.Id);
        await client.AddLinkAsync(ws, named.Id, "https://www.facebook.com/groups/plants");
        var demo = await client.PostAsJsonAsync($"/api/workspaces/{ws}/schedules", new { collectionId = collection.Id, linkSetId = named.Id, mode = "daily", times = new[] { "10:00" }, order = "rotate", utcOffsetMinutes = Bangkok }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, demo.StatusCode);
    }

    [Fact]
    public async Task A_set_with_nothing_to_post_to_is_refused()
    {
        using var shop = await factory.ShopAsync(links: 0);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        await shop.Owner.AddLinkAsync(shop.Ws, shop.Set.Id, "https://example.com/not-a-group");

        var res = await shop.TryCreateScheduleAsync(Daily(slot));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task A_collection_or_link_set_that_is_not_there_is_refused()
    {
        using var shop = await factory.ShopAsync();
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var spec = Daily(slot);

        var noCollection = await shop.Owner.PostAsJsonAsync($"{shop.Api}/schedules", shop.Body(spec, collection: Guid.NewGuid()), Json);
        var noSet = await shop.Owner.PostAsJsonAsync($"{shop.Api}/schedules", shop.Body(spec, set: Guid.NewGuid()), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCollection.StatusCode);
        Assert.Contains("ชุดโพสต์", await TitleAsync(noCollection));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSet.StatusCode);
        Assert.Contains("ชุดลิงก์", await TitleAsync(noSet));

        // Another workspace's collection is as good as missing.
        using var other = await factory.ShopAsync();
        var foreign = await shop.Owner.PostAsJsonAsync($"{shop.Api}/schedules", shop.Body(spec, collection: other.Collection.Id), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
    }

    [Fact]
    public async Task A_workspace_has_at_most_fifty_schedules()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 1);
        var tomorrow = LocalDay(shop.Now.AddDays(1));
        var spec = new ScheduleSpec(Mode: "once", StartDate: tomorrow, Offset: Bangkok);

        for (var i = 0; i < 50; i++) (await shop.TryCreateScheduleAsync(spec with { Name = $"ตาราง {i}" })).EnsureSuccessStatusCode();
        var fiftyFirst = await shop.TryCreateScheduleAsync(spec with { Name = "เกิน" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, fiftyFirst.StatusCode);
        Assert.Contains("50", await TitleAsync(fiftyFirst));
        Assert.Equal(50, (await shop.SchedulesAsync()).Count);
    }

    [Fact]
    public async Task A_bad_request_is_a_400_in_thai()
    {
        using var shop = await factory.ShopAsync();
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        async Task<ValidationProblemDetails> Bad(ScheduleSpec spec)
        {
            var res = await shop.TryCreateScheduleAsync(spec);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<ValidationProblemDetails>(Json))!;
        }

        Assert.Contains("times", (await Bad(Daily(slot) with { Times = ["25:00"] })).Errors.Keys);
        Assert.Contains("startDate", (await Bad(Daily(slot) with { StartDate = "03/10/2026" })).Errors.Keys);
        Assert.Contains("utcOffsetMinutes", (await Bad(Daily(slot) with { Offset = 1000 })).Errors.Keys);
        Assert.Contains("bumpHours", (await Bad(Daily(slot) with { BumpHours = 5 })).Errors.Keys);
        Assert.Contains("autoDeleteDays", (await Bad(Daily(slot) with { AutoDeleteDays = 2 })).Errors.Keys);
        Assert.Contains("everyHours", (await Bad(new ScheduleSpec(Mode: "interval", EveryHours: 0, Offset: Bangkok))).Errors.Keys);
        Assert.Contains("dripCount", (await Bad(new ScheduleSpec(Mode: "drip", DripCount: 13, Offset: Bangkok))).Errors.Keys);
        Assert.Contains("overrides", (await Bad(Daily(slot) with { Overrides = new() { ["not-an-id"] = ["10:00"] } })).Errors.Keys);
        var tooLong = await Bad(Daily(slot) with { Name = new string('ก', 121) });
        Assert.Contains("ชื่อตาราง", tooLong.Errors["name"][0]);

        var badMode = await shop.Owner.PostAsJsonAsync($"{shop.Api}/schedules", shop.Body(Daily(slot) with { Mode = "yearly" }), Json);
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
        Assert.Empty((await shop.SchedulesAsync()));
    }

    [Fact]
    public async Task A_drip_that_ends_before_it_starts_is_a_domain_error()
    {
        using var shop = await factory.ShopAsync();

        var res = await shop.TryCreateScheduleAsync(new ScheduleSpec(Mode: "drip", DripFrom: "21:00", DripTo: "09:00", Offset: Bangkok));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    // ---- pause, resume, delete ----

    [Fact]
    public async Task Pausing_drops_the_queued_future_posts_and_resuming_makes_them_again_without_touching_the_history()
    {
        using var shop = await factory.ShopAsync(links: 2);
        await shop.SetAntiBanAsync(a => a with { Shuffle = false }); // a fixed order: the second group's time is a few minutes after the first
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        var id = created.Schedule.Id;

        // One post goes out first.
        shop.WaitUntil(created.FirstAt!.Value);
        var done = (await shop.RunNextAsync())!;
        Assert.Equal(PostStatus.Success, done.Status);

        var paused = await (await shop.SetActiveAsync(id, false)).ReadAsync<ScheduleDto>();
        Assert.False(paused.Active);
        Assert.Null(paused.NextRunAt);
        var left = await shop.PostsAsync(id);
        Assert.Equal([done.Id], left.Select(p => p.Id)); // only what went out stays
        Assert.False((await shop.SchedulesAsync()).Single().Active);

        // Pausing again changes nothing.
        Assert.False((await (await shop.SetActiveAsync(id, false)).ReadAsync<ScheduleDto>()).Active);

        var resumed = await (await shop.SetActiveAsync(id, true)).ReadAsync<ScheduleDto>();
        Assert.True(resumed.Active);
        Assert.NotNull(resumed.NextRunAt);
        var again = await shop.PostsAsync(id);
        Assert.Equal(created.Created, again.Count); // the sent post is not made twice; the rest come back
        Assert.Equal(created.Created - 1, again.Count(p => p.Status == PostStatus.Queued));
        Assert.Single(again, p => p.Id == done.Id);
    }

    [Fact]
    public async Task Deleting_removes_the_queued_future_posts_and_the_schedule_but_not_what_was_sent()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        shop.WaitUntil(created.FirstAt!.Value);
        var sent = (await shop.RunNextAsync())!;

        var res = await shop.Owner.DeleteAsync($"{shop.Api}/schedules/{created.Schedule.Id}");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(await shop.SchedulesAsync());
        var remaining = (await shop.PostsAsync()).Where(p => p.ScheduleId == created.Schedule.Id).ToList();
        Assert.Equal([sent.Id], remaining.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await shop.Owner.DeleteAsync($"{shop.Api}/schedules/{created.Schedule.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shop.SetActiveAsync(created.Schedule.Id, true)).StatusCode);
    }

    [Fact]
    public async Task A_collection_or_set_in_use_by_a_schedule_cannot_be_deleted()
    {
        using var shop = await factory.ShopAsync();
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot, name: "ตารางเช้า"));

        var collection = await shop.Owner.DeleteAsync($"{shop.Api}/collections/{shop.Collection.Id}");
        var set = await shop.Owner.DeleteAsync($"{shop.Api}/link-sets/{shop.Set.Id}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, collection.StatusCode);
        Assert.Contains("ตารางเช้า", await TitleAsync(collection));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, set.StatusCode);
        var shown = (await shop.Owner.CollectionsAsync(shop.Ws)).Single();
        Assert.Equal(1, shown.ScheduleCount);
        Assert.Equal(1, (await shop.Owner.LinkSetsAsync(shop.Ws)).Single().ScheduleCount);
        Assert.Equal(created.Schedule.Id, (await shop.SchedulesAsync()).Single().Id);
    }

    // ---- roles ----

    [Fact]
    public async Task Viewers_read_editors_write_and_other_workspaces_are_invisible()
    {
        using var shop = await factory.ShopAsync("agency", links: 1);
        var viewer = await JoinAsync(shop, "viewer");
        var editor = await JoinAsync(shop, "editor");
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));

        Assert.Equal(HttpStatusCode.Forbidden, (await shop.TryCreateScheduleAsync(Daily(slot), viewer)).StatusCode);
        var created = await (await shop.TryCreateScheduleAsync(Daily(slot), editor)).ReadAsync<ScheduleCreatedDto>();

        Assert.Single(await shop.SchedulesAsync(viewer));
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.SetActiveAsync(created.Schedule.Id, false, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"{shop.Api}/schedules/{created.Schedule.Id}")).StatusCode);
        Assert.True((await shop.SetActiveAsync(created.Schedule.Id, false, editor)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{shop.Api}/schedules/best-times")).StatusCode);

        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{shop.Api}/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shop.TryCreateScheduleAsync(Daily(slot), stranger)).StatusCode);
    }

    private async Task<HttpClient> JoinAsync(Shop shop, string role)
    {
        var (client, auth, _) = await factory.SignUpAsync();
        (await shop.Owner.PostAsJsonAsync($"{shop.Api}/members", new { email = auth.User.Email, role }, Json)).EnsureSuccessStatusCode();
        return client;
    }

    // ---- the engine keeps schedules ahead of the clock ----

    [Fact]
    public async Task A_device_claim_tops_the_schedule_up_when_days_have_passed_and_does_it_once()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        var before = await shop.PostsAsync(created.Schedule.Id);

        shop.Wait(TimeSpan.FromDays(3)); // the first three days are over; the fortnight needs three new days
        Assert.Null(await shop.ClaimAsync()); // the old ones are too late for the offline policy; nothing new is due yet

        var after = await shop.PostsAsync(created.Schedule.Id);
        // Three new days, two groups each, queued after the last post of the first fortnight.
        var added = after.Where(p => p.ScheduledAt > created.LastAt).ToList();
        Assert.Equal(3 * 2, added.Count);
        Assert.All(added, p => Assert.Equal(PostStatus.Queued, p.Status));
        Assert.True(added.Max(p => p.ScheduledAt) >= created.LastAt!.Value.AddDays(2.9));
        Assert.All(after.Where(p => p.ScheduledAt < shop.Now), p => Assert.Equal(PostStatus.Skipped, p.Status)); // the days that passed

        // A second claim finds nothing to add.
        Assert.Null(await shop.ClaimAsync());
        Assert.Equal(after.Count, (await shop.PostsAsync(created.Schedule.Id)).Count);
    }

    [Fact]
    public async Task Claims_that_arrive_together_after_days_have_passed_top_up_once_and_nobody_gets_an_error()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        shop.Wait(TimeSpan.FromDays(3));

        // Eight claims race to fill the same three days: the unique index lets one of them win each slot.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shop.Device.PostAsync("/api/device/jobs/claim", null)));

        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, r.StatusCode.ToString()));
        var added = (await shop.PostsAsync(created.Schedule.Id)).Where(p => p.ScheduledAt > created.LastAt).ToList();
        Assert.Equal(3 * 2, added.Count);
        Assert.Equal(6, added.Select(p => (p.LinkId, LocalDay(p.ScheduledAt))).Distinct().Count());
    }

    [Fact]
    public async Task Seventy_two_groups_three_times_a_day_starting_now_are_made_two_thousand_at_most_at_a_time_and_a_claim_makes_the_rest()
    {
        // 216 tasks a day for a fortnight is more than one run makes (2,000): creating makes the days that fit.
        using var shop = await factory.ShopAsync(links: 72);
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["09:00", "13:00", "18:00"], Order: "shuffle", Offset: Bangkok, StartNow: true));

        Assert.Equal((72, 216), (created.Schedule.TargetCount, created.Schedule.PerDay));
        Assert.InRange(created.Created, 72 + 216, Schedule.MaxPostsPerRun);
        Assert.Equal(created.Created, (await shop.PostsAsync(created.Schedule.Id)).Count);

        // The browser's next claim tops the schedule up with the days that did not fit.
        await shop.ClaimAsync();

        var slots = await factory.WithDbAsync(db => db.Posts.Where(p => p.ScheduleId == created.Schedule.Id)
            .GroupBy(p => p.SlotKey!).Select(g => new { Slot = g.Key, Count = g.Count(), Groups = g.Select(p => p.LinkId).Distinct().Count() })
            .ToListAsync());
        var today = DateOnly.ParseExact(LocalDay(shop.Now), "yyyy-MM-dd");
        Assert.Equal((72, 72), slots.Where(s => s.Slot.EndsWith("Tnow")).Select(s => (s.Count, s.Groups)).Single());
        for (var day = today.AddDays(1); day <= today.AddDays(Schedule.HorizonDays - 1); day = day.AddDays(1))
            foreach (var time in new[] { "09:00", "13:00", "18:00" })
                Assert.Equal((72, 72), slots.Where(s => s.Slot == Schedule.SlotKey(day, time)).Select(s => (s.Count, s.Groups)).Single());
        Assert.True(slots.Sum(s => s.Count) > Schedule.MaxPostsPerRun);

        // Nothing is left to make: another claim adds nothing.
        var total = slots.Sum(s => s.Count);
        await shop.ClaimAsync();
        Assert.Equal(total, await factory.WithDbAsync(db => db.Posts.CountAsync(p => p.ScheduleId == created.Schedule.Id)));
    }

    [Fact]
    public async Task A_schedule_that_is_paused_is_not_topped_up_and_a_once_schedule_whose_day_has_passed_is_switched_off()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var daily = await shop.CreateScheduleAsync(Daily(slot));
        await shop.SetActiveAsync(daily.Schedule.Id, false);
        var once = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: LocalDay(shop.Now.AddDays(1)), Offset: Bangkok));

        shop.Wait(TimeSpan.FromDays(3));
        await shop.ClaimAsync();

        Assert.Empty(await shop.PostsAsync(daily.Schedule.Id)); // still paused
        var schedules = await shop.SchedulesAsync();
        Assert.False(schedules.Single(s => s.Id == daily.Schedule.Id).Active);
        Assert.False(schedules.Single(s => s.Id == once.Schedule.Id).Active); // done: its day is over
    }

    // ---- best times ----

    [Fact]
    public async Task Best_times_are_the_three_busiest_hours_of_the_last_month_in_order()
    {
        using var shop = await factory.ShopAsync(links: 1);
        Assert.Empty((await shop.Owner.GetFromJsonAsync<List<string>>($"{shop.Api}/schedules/best-times?utcOffsetMinutes=420", Json))!);

        // Yesterday: 3 posts at 03h UTC, 2 at 09h, 2 at 14h and 1 at 21h. A busier hour more than 30 days ago does not count.
        var yesterday = shop.Now.UtcDateTime.Date.AddDays(-1);
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.FindAsync(shop.Pair.AccountId);
            foreach (var (hour, count) in new[] { (3, 3), (9, 2), (14, 2), (21, 1) })
                for (var i = 0; i < count; i++)
                    db.Posts.Add(Post.Record(shop.Ws, account!, "กลุ่ม", "x", new DateTimeOffset(yesterday.AddHours(hour).AddMinutes(10 * i), TimeSpan.Zero), PostStatus.Success, null, shop.Now));
            for (var i = 0; i < 5; i++)
                db.Posts.Add(Post.Record(shop.Ws, account!, "กลุ่ม", "x", new DateTimeOffset(yesterday.AddDays(-40).AddHours(11), TimeSpan.Zero), PostStatus.Success, null, shop.Now));
            await db.SaveChangesAsync();
        });

        async Task<List<string>> Best(int offset) =>
            (await shop.Owner.GetFromJsonAsync<List<string>>($"{shop.Api}/schedules/best-times?utcOffsetMinutes={offset}", Json))!;

        Assert.Equal(["03:00", "09:00", "14:00"], await Best(0));
        Assert.Equal(["10:00", "16:00", "21:00"], await Best(420)); // the same posts in Bangkok's hours
        Assert.Equal(["04:00", "09:00", "22:00"], await Best(-300)); // and in New York's, where 03h is last evening
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.Owner.GetAsync($"{shop.Api}/schedules/best-times?utcOffsetMinutes=1000")).StatusCode);
    }
}
