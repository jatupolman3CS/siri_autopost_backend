using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The rules the engine applies when a browser claims a post and reports on it: the link checks, the advanced anti-ban
// numbers, link health and the device's own pauses. Posts come from the test page (due at once), so a rule can be tried
// without waiting for a schedule; the failure-rate rule needs real schedule posts (tests are left out of the rate).
[Collection(ApiCollection.Name)]
public class EngineClaimTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static string Str(DeviceEventDto e, string name) => e.Payload.GetProperty(name).GetString()!;

    private static bool Has(DeviceEventDto e, string name) => e.Payload.TryGetProperty(name, out _);

    /// <summary>A test post to a link, claimed and reported; then the anti-ban gap passes.</summary>
    private static async Task<PostDto> RunAsync(
        Shop shop, int link = 0, bool ok = true, bool blocked = false, bool needsLogin = false, bool awaitingApproval = false)
    {
        await shop.TestPostAsync(link);
        var done = await shop.RunNextAsync(ok, blocked, needsLogin, awaitingApproval);
        shop.Wait(TimeSpan.FromMinutes(5));
        return done!;
    }

    // ---- the address ----

    [Fact]
    public async Task A_post_from_a_link_goes_to_the_links_address_without_the_browser_syncing_groups()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 1, footer: "ติดต่อ 081-234-5678");
        Assert.Empty((await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"{shop.Api}/accounts", Json))!.Single(a => a.Id == shop.Pair.AccountId).Groups);
        var post = await shop.TestPostAsync(1);

        var job = (await shop.ClaimAsync())!;

        Assert.Equal((post.Id, shop.Link(1).Url, "กลุ่ม 1"), (job.PostId, job.GroupUrl, job.GroupName));
        Assert.Equal("C1\nโพสต์ 1\n\nติดต่อ 081-234-5678", job.Content);
    }

    // ---- the link ----

    [Fact]
    public async Task A_post_for_a_link_that_was_switched_off_is_skipped_and_the_next_one_goes_out()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var off = await shop.TestPostAsync(0);
        var other = await shop.TestPostAsync(1);
        await shop.UpdateLinkAsync(0, enabled: false);
        var head = await shop.HeadAsync();

        var job = (await shop.ClaimAsync())!;

        Assert.Equal(other.Id, job.PostId);
        var skipped = await shop.PostAsync(off.Id);
        Assert.Equal((PostStatus.Skipped, "กลุ่มถูกปิดหรือถูกลบแล้ว"), (skipped.Status, skipped.FailureDetail));
        var events = await shop.EventsAsync("post", head);
        Assert.Contains(events, e => e.Payload.GetProperty("postId").GetGuid() == off.Id && Str(e, "status") == "skipped");
    }

    [Fact]
    public async Task A_post_for_a_link_that_was_deleted_is_skipped_too()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var post = await shop.TestPostAsync(0);
        (await shop.Owner.DeleteAsync($"{shop.Api}/link-sets/{shop.Set.Id}/links/{shop.Link(0).Id}")).EnsureSuccessStatusCode();

        Assert.Null(await shop.ClaimAsync());

        var skipped = await shop.PostAsync(post.Id);
        Assert.Equal((PostStatus.Skipped, "กลุ่มถูกปิดหรือถูกลบแล้ว"), (skipped.Status, skipped.FailureDetail));
    }

    [Fact]
    public async Task A_group_takes_no_more_posts_than_its_daily_cap_in_24_hours()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await shop.UpdateLinkAsync(0, dailyMax: 1);
        Assert.Equal(PostStatus.Success, (await RunAsync(shop)).Status);

        var second = await shop.TestPostAsync(0);
        Assert.Null(await shop.ClaimAsync());
        var skipped = await shop.PostAsync(second.Id);
        Assert.Equal((PostStatus.Skipped, "ครบเพดานต่อวันของกลุ่มนี้"), (skipped.Status, skipped.FailureDetail));

        shop.Wait(TimeSpan.FromHours(25)); // a day on, the cap is free again
        await shop.TestPostAsync(0);
        Assert.Equal(PostStatus.Success, (await shop.RunNextAsync())!.Status);
    }

    [Fact]
    public async Task A_cooldown_keeps_a_post_queued_until_the_group_has_rested_while_other_groups_go_on()
    {
        using var shop = await factory.ShopAsync(links: 2);
        await shop.SetAdvancedAsync(a => a with { Cooldown = 2 });
        await RunAsync(shop, 0);

        var held = await shop.TestPostAsync(0);
        var free = await shop.TestPostAsync(1);
        var job = (await shop.ClaimAsync())!;
        Assert.Equal(free.Id, job.PostId); // link 0 is resting; link 1 is not
        await shop.ReportAsync(job.PostId);
        shop.Wait(TimeSpan.FromMinutes(5));

        Assert.Null(await shop.ClaimAsync());
        Assert.Equal(PostStatus.Queued, (await shop.PostAsync(held.Id)).Status); // waits, is not skipped

        shop.Wait(TimeSpan.FromHours(2)); // the cooldown is over; the wait does not count as lateness
        Assert.Equal(held.Id, (await shop.ClaimAsync())!.PostId);
    }

    // ---- the advanced numbers ----

    [Fact]
    public async Task The_larger_of_the_smart_delay_and_the_minimum_gap_is_the_gap_between_two_posts()
    {
        using var shop = await factory.ShopAsync();
        await shop.SetAdvancedAsync(a => a with { MinGap = 10 }); // the smart delay's minimum is 3
        await RunAsync(shop); // and 5 minutes pass
        var next = await shop.TestPostAsync();

        Assert.Null(await shop.ClaimAsync()); // 5 < 10
        shop.Wait(TimeSpan.FromMinutes(6));
        Assert.Equal(next.Id, (await shop.ClaimAsync())!.PostId);
    }

    [Fact]
    public async Task The_daily_cap_over_all_platforms_fails_the_post_as_a_quota_error()
    {
        using var shop = await factory.ShopAsync();
        await shop.SetAdvancedAsync(a => a with { DailyAll = 2 });
        await RunAsync(shop, 0);
        await RunAsync(shop, 1);
        var third = await shop.TestPostAsync(0);

        Assert.Null(await shop.ClaimAsync());

        var failed = await shop.PostAsync(third.Id);
        Assert.Equal((PostStatus.Failed, FailureCode.Quota), (failed.Status, failed.FailureCode));
        Assert.Contains("ครบเพดานรวม 2", failed.FailureDetail);
    }

    [Theory]
    [InlineData(6, 4, true)] // 4 of the last 10 failed: more than 30%
    [InlineData(7, 3, false)] // exactly 30%
    [InlineData(1, 5, false)] // 83%, but only 6 posts finished: too few to say
    public async Task The_engine_stops_when_too_many_of_the_last_days_posts_failed(int succeeded, int failed, bool stopped)
    {
        using var shop = await factory.ShopAsync(links: 12, posts: 1);
        await shop.SetAdvancedAsync(a => a with { FailStreak = 0, AutoOffFails = 0, StopFailPct = 30 });
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/engine/offline", new { policy = "queue", window = "day", line = true, email = true, push = false }, Json)).EnsureSuccessStatusCode();
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: ToLocalDay(shop.Now.AddDays(1), 0), OnceTime: "10:00", Offset: 0));
        Assert.Equal(12, created.Created);
        shop.WaitUntil(created.LastAt!.Value); // all twelve are due

        for (var i = 0; i < succeeded + failed; i++)
        {
            Assert.NotNull(await shop.RunNextAsync(ok: i < succeeded));
            shop.Wait(TimeSpan.FromMinutes(5));
        }

        var job = await shop.ClaimAsync();
        Assert.Equal(stopped, job is null);
        if (stopped)
        {
            var queued = (await shop.PostsAsync(created.Schedule.Id)).Count(p => p.Status == PostStatus.Queued);
            Assert.Equal(12 - succeeded - failed, queued); // they wait: nothing was failed or skipped
        }
    }

    [Fact]
    public async Task While_warm_up_is_on_a_new_browser_posts_only_three_a_day_then_six_then_twelve()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await shop.SetAntiBanAsync(a => a with { Warmup = true });
        for (var i = 0; i < 3; i++) Assert.Equal(PostStatus.Success, (await RunAsync(shop)).Status);

        var fourth = await shop.TestPostAsync();
        Assert.Null(await shop.ClaimAsync());
        var failed = await shop.PostAsync(fourth.Id);
        Assert.Equal((PostStatus.Failed, FailureCode.Quota), (failed.Status, failed.FailureCode));
        Assert.Contains("ช่วงอุ่นเครื่อง 3", failed.FailureDetail);

        shop.Wait(TimeSpan.FromDays(5)); // five days old: six a day
        for (var i = 0; i < 6; i++) Assert.Equal(PostStatus.Success, (await RunAsync(shop)).Status);
        var seventh = await shop.TestPostAsync();
        Assert.Null(await shop.ClaimAsync());
        Assert.Contains("ช่วงอุ่นเครื่อง 6", (await shop.PostAsync(seventh.Id)).FailureDetail);

        shop.Wait(TimeSpan.FromDays(10)); // two weeks old: the platform's own limit again
        Assert.Equal(PostStatus.Success, (await RunAsync(shop)).Status);
    }

    [Fact]
    public async Task Without_warm_up_a_new_browser_posts_up_to_the_platforms_limit()
    {
        using var shop = await factory.ShopAsync(links: 1);
        for (var i = 0; i < 4; i++) Assert.Equal(PostStatus.Success, (await RunAsync(shop)).Status);
    }

    // ---- link health ----

    [Fact]
    public async Task A_group_that_keeps_failing_is_switched_off_and_announced_and_its_waiting_posts_are_skipped()
    {
        using var shop = await factory.ShopAsync(links: 2); // default: off after 3 failures in a row
        var head = await shop.HeadAsync();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(PostStatus.Failed, (await RunAsync(shop, 0, ok: false)).Status);
            Assert.Equal(i + 1, (await shop.LinkNowAsync(0)).FailStreak);
        }

        var link = await shop.LinkNowAsync(0);
        Assert.Equal((false, LinkHealth.Off, 3), (link.Enabled, link.Health, link.FailStreak));
        var changed = Assert.Single(await shop.EventsAsync("links.changed", head));
        Assert.Equal((shop.Set.Id.ToString(), shop.Link(0).Id.ToString(), "off"), (Str(changed, "linkSetId"), Str(changed, "linkId"), Str(changed, "health")));
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil); // three is fewer than the four that pause a device

        var waiting = await shop.TestPostAsync(1); // the other group is fine
        var late = shop.Owner.PostAsJsonAsync($"{shop.Api}/test-post", new { linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await late).StatusCode); // a test to a group that is off is refused
        Assert.Equal(waiting.Id, (await shop.ClaimAsync())!.PostId);

        // "Enable again" brings it back.
        var enabled = await (await shop.Owner.PostAsync($"{shop.Api}/link-sets/{shop.Set.Id}/links/{shop.Link(0).Id}/enable", null)).ReadAsync<SetLinkDto>();
        Assert.Equal((true, LinkHealth.Ok, 0), (enabled.Enabled, enabled.Health, enabled.FailStreak));
    }

    [Fact]
    public async Task A_post_waiting_for_a_group_that_just_went_off_is_skipped_at_the_next_claim()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var waiting = new List<PostDto>();
        for (var i = 0; i < 4; i++) waiting.Add(await shop.TestPostAsync(0)); // all due now

        for (var i = 0; i < 3; i++) // the first three fail, in order
        {
            Assert.Equal(waiting[i].Id, (await shop.RunNextAsync(ok: false))!.Id);
            shop.Wait(TimeSpan.FromMinutes(5));
        }

        Assert.Null(await shop.ClaimAsync());
        Assert.Equal((PostStatus.Skipped, "กลุ่มถูกปิดหรือถูกลบแล้ว"), ((await shop.PostAsync(waiting[3].Id)).Status, (await shop.PostAsync(waiting[3].Id)).FailureDetail));
    }

    [Fact]
    public async Task A_success_starts_the_count_again()
    {
        using var shop = await factory.ShopAsync(links: 1);
        foreach (var ok in new[] { false, false, true, false, false })
        {
            await RunAsync(shop, 0, ok);
        }

        var link = await shop.LinkNowAsync(0);
        Assert.Equal((true, 2), (link.Enabled, link.FailStreak));
    }

    [Fact]
    public async Task Failures_of_the_account_do_not_count_against_the_group()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await RunAsync(shop, 0, ok: false, blocked: true);
        Assert.Equal((true, 0), ((await shop.LinkNowAsync(0)).Enabled, (await shop.LinkNowAsync(0)).FailStreak));

        // A lost login: the account asks for a new login, which is not the group's fault either.
        shop.Wait(TimeSpan.FromHours(50)); // the block's pause is over
        await RunAsync(shop, 0, ok: false, needsLogin: true);
        Assert.Equal(0, (await shop.LinkNowAsync(0)).FailStreak);
    }

    [Fact]
    public async Task Auto_off_and_the_failure_pause_can_be_turned_off()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await shop.SetAdvancedAsync(a => a with { AutoOffFails = 0, FailStreak = 0 });

        for (var i = 0; i < 5; i++) await RunAsync(shop, 0, ok: false);

        var link = await shop.LinkNowAsync(0);
        Assert.Equal((true, 5), (link.Enabled, link.FailStreak));
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil);
    }

    [Fact]
    public async Task A_group_that_holds_posts_for_approval_is_marked_pending_and_clears_with_the_next_success()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var head = await shop.HeadAsync();

        var pending = await RunAsync(shop, 0, awaitingApproval: true);
        Assert.Equal(PostStatus.Pending, pending.Status);
        var link = await shop.LinkNowAsync(0);
        Assert.Equal((LinkHealth.Pending, true), (link.Health, link.Enabled));
        Assert.Equal("pending", Str(Assert.Single(await shop.EventsAsync("links.changed", head)), "health"));

        await RunAsync(shop, 0);
        Assert.Equal(LinkHealth.Ok, (await shop.LinkNowAsync(0)).Health);
        Assert.Equal(["pending", "ok"], (await shop.EventsAsync("links.changed", head)).Select(e => Str(e, "health")));
    }

    // ---- the device's own pause ----

    [Fact]
    public async Task A_facebook_block_pauses_the_device_for_a_day_or_two_and_the_pause_ends_by_itself()
    {
        using var shop = await factory.ShopAsync(links: 1);
        var head = await shop.HeadAsync();
        var start = shop.Now;

        var blocked = await RunAsync(shop, 0, ok: false, blocked: true);

        Assert.Equal((PostStatus.Failed, FailureCode.RateLimit), (blocked.Status, blocked.FailureCode));
        var device = await shop.DeviceAsync();
        Assert.InRange(device.AutoPausedUntil!.Value, start.AddHours(24), start.AddHours(48).AddMinutes(10));
        Assert.Contains("Facebook", device.AutoPauseReason);
        Assert.False(device.JobsPaused); // the web's own switch is not touched
        var paused = Assert.Single(await shop.EventsAsync("device.updated", head));
        Assert.True(Has(paused, "autoPausedUntil"));

        // The browser is told, and takes nothing.
        var status = await shop.HeartbeatAsync();
        Assert.True(status.JobsPaused);
        Assert.Equal(device.AutoPausedUntil, status.AutoPausedUntil);
        var waiting = await shop.TestPostAsync();
        Assert.Null(await shop.ClaimAsync());
        Assert.Equal(PostStatus.Queued, (await shop.PostAsync(waiting.Id)).Status);

        shop.Wait(TimeSpan.FromHours(49));
        status = await shop.HeartbeatAsync();
        Assert.False(status.JobsPaused);
        Assert.Null(status.AutoPausedUntil);
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil);
        var events = await shop.EventsAsync("device.updated", head);
        Assert.Equal(2, events.Count);
        Assert.False(Has(events[1], "autoPausedUntil")); // the second event says it ended

        await shop.TestPostAsync(); // the one that waited is long past; a new one goes
        Assert.Equal(PostStatus.Success, (await shop.RunNextAsync())!.Status);
    }

    [Fact]
    public async Task A_claim_after_the_pause_notices_it_is_over_and_says_so_once()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await shop.SetAdvancedAsync(a => a with { BlockMin = 1, BlockMax = 1 });
        await RunAsync(shop, 0, ok: false, blocked: true);
        var until = (await shop.DeviceAsync()).AutoPausedUntil!.Value;
        Assert.InRange(until, shop.Now, shop.Now.AddHours(1));
        var head = await shop.HeadAsync();

        shop.Wait(TimeSpan.FromMinutes(61));
        await shop.TestPostAsync();
        Assert.Equal(PostStatus.Success, (await shop.RunNextAsync())!.Status);

        var ended = Assert.Single(await shop.EventsAsync("device.updated", head));
        Assert.False(Has(ended, "autoPausedUntil"));
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil);
    }

    [Fact]
    public async Task With_auto_pause_off_a_block_does_not_pause_the_device()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await shop.SetAntiBanAsync(a => a with { AutoPause = false });

        var blocked = await RunAsync(shop, 0, ok: false, blocked: true);

        Assert.Equal(FailureCode.RateLimit, blocked.FailureCode);
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil);
        Assert.False((await shop.HeartbeatAsync()).JobsPaused);
    }

    [Fact]
    public async Task The_owner_resuming_the_device_lifts_the_pause()
    {
        using var shop = await factory.ShopAsync(links: 1);
        await RunAsync(shop, 0, ok: false, blocked: true);
        var device = await shop.DeviceAsync();
        Assert.NotNull(device.AutoPausedUntil);

        var res = await shop.Owner.PutAsJsonAsync($"{shop.Api}/devices/{device.Id}", new { jobsPaused = false }, Json);

        Assert.Null((await res.ReadAsync<DeviceDto>()).AutoPausedUntil);
        await shop.TestPostAsync();
        Assert.Equal(PostStatus.Success, (await shop.RunNextAsync())!.Status);
    }

    [Fact]
    public async Task Failed_posts_in_a_row_rest_the_device_for_two_to_four_hours()
    {
        using var shop = await factory.ShopAsync(links: 2); // default: 4 in a row; the links alternate so none is switched off
        for (var i = 0; i < 3; i++) await RunAsync(shop, i % 2, ok: false);
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil);
        var head = await shop.HeadAsync();
        var start = shop.Now;

        await RunAsync(shop, 1, ok: false);

        var device = await shop.DeviceAsync();
        Assert.InRange(device.AutoPausedUntil!.Value, start.AddHours(2), start.AddHours(4).AddMinutes(10));
        Assert.Contains("ล้มเหลวติดกัน 4", device.AutoPauseReason);
        Assert.Single(await shop.EventsAsync("device.updated", head));
        await shop.TestPostAsync();
        Assert.Null(await shop.ClaimAsync());

        shop.Wait(TimeSpan.FromHours(5));
        await shop.TestPostAsync();
        Assert.Equal(PostStatus.Success, (await shop.RunNextAsync())!.Status);
    }

    [Fact]
    public async Task A_success_in_between_breaks_the_run_and_the_number_is_the_owners_to_set()
    {
        using var shop = await factory.ShopAsync(links: 2);
        await shop.SetAdvancedAsync(a => a with { FailStreak = 2, AutoOffFails = 0 });

        await RunAsync(shop, 0, ok: false);
        await RunAsync(shop, 0);
        await RunAsync(shop, 0, ok: false);
        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil); // fail, ok, fail: not two in a row

        var start = shop.Now;
        await RunAsync(shop, 0, ok: false);
        Assert.InRange((await shop.DeviceAsync()).AutoPausedUntil!.Value, start.AddHours(2), start.AddHours(4).AddMinutes(10));
    }

    [Fact]
    public async Task The_failure_pause_is_off_at_zero()
    {
        using var shop = await factory.ShopAsync(links: 2);
        await shop.SetAdvancedAsync(a => a with { FailStreak = 0, AutoOffFails = 0 });

        for (var i = 0; i < 6; i++) await RunAsync(shop, i % 2, ok: false);

        Assert.Null((await shop.DeviceAsync()).AutoPausedUntil);
    }
}
