using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

internal sealed record Sent(NotifyEvent Event, Guid WorkspaceId, Guid? LinkSetId, Guid? LinkId, string Text);

/// <summary>Remembers what the engine asked to be sent.</summary>
internal sealed class RecordingNotifier : INotificationDispatcher
{
    public ConcurrentQueue<Sent> Messages { get; } = new();

    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default)
    {
        Messages.Enqueue(new Sent(ev, workspaceId, linkSetId, linkId, text));
        return Task.CompletedTask;
    }

    public List<Sent> Of(NotifyEvent ev) => Messages.Where(m => m.Event == ev).ToList();
}

internal sealed class ThrowingNotifier : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default) =>
        throw new InvalidOperationException("Telegram is down");
}

// What the engine tells the notification system, through a host whose dispatcher is a recorder (the same database and clock).
[Collection(ApiCollection.Name)]
public class EngineNotificationTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private WebApplicationFactory<Program> HostWith(INotificationDispatcher notifier) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<INotificationDispatcher>();
            s.AddSingleton(notifier);
        }));

    private async Task<(Shop Shop, RecordingNotifier Notifier, WebApplicationFactory<Program> Host)> StartAsync(int links = 2, int posts = 2)
    {
        var notifier = new RecordingNotifier();
        var host = HostWith(notifier);
        return (await host.ShopAsync(factory.Clock, links: links, posts: posts), notifier, host);
    }

    private static async Task<PostDto> RunAsync(Shop shop, int link = 0, bool ok = true, bool blocked = false, bool needsLogin = false, bool awaitingApproval = false)
    {
        await shop.TestPostAsync(link);
        var done = await shop.RunNextAsync(ok, blocked, needsLogin, awaitingApproval, ok ? null : "โดนจำกัดการโพสต์");
        shop.Wait(TimeSpan.FromMinutes(5));
        return done!;
    }

    /// <summary>Twelve posts at 10:00 tomorrow in a schedule, all due, with a window long enough to claim them late.</summary>
    private static async Task<(ScheduleCreatedDto Created, int Links)> DueScheduleAsync(Shop shop, int links)
    {
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/engine/offline", new { policy = "queue", window = "day", line = true, email = true, push = false }, Json)).EnsureSuccessStatusCode();
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: ToLocalDay(shop.Now.AddDays(1), 0), OnceTime: "10:00", Offset: 0, Name: "ตารางเช้า"));
        Assert.Equal(links, created.Created);
        shop.WaitUntil(created.LastAt!.Value);
        return (created, links);
    }

    [Fact]
    public async Task A_success_is_announced_with_the_group_its_code_and_the_start_of_the_post()
    {
        var (shop, notifier, host) = await StartAsync();
        using var _ = shop;
        await using var __ = host;

        await RunAsync(shop, 1);

        var sent = Assert.Single(notifier.Of(NotifyEvent.Success));
        Assert.Equal((shop.Ws, shop.Set.Id, shop.Link(1).Id), (sent.WorkspaceId, sent.LinkSetId, sent.LinkId));
        Assert.Contains("โพสต์สำเร็จ", sent.Text);
        Assert.Contains("กลุ่ม 1 (C1)", sent.Text);
        Assert.Contains("โพสต์", sent.Text.Split('“')[1]);
        Assert.Single(notifier.Messages); // nothing else
    }

    [Fact]
    public async Task A_failure_says_why_and_a_group_that_waits_for_approval_is_a_success_with_a_note()
    {
        var (shop, notifier, host) = await StartAsync();
        using var _ = shop;
        await using var __ = host;

        await RunAsync(shop, 0, ok: false);
        await RunAsync(shop, 1, awaitingApproval: true);

        var failed = Assert.Single(notifier.Of(NotifyEvent.Fail));
        Assert.Contains("โพสต์ล้มเหลว", failed.Text);
        Assert.Contains("โดนจำกัดการโพสต์", failed.Text);
        Assert.Contains("กลุ่ม 0 (C0)", failed.Text);
        Assert.Equal(shop.Link(0).Id, failed.LinkId);
        var pending = Assert.Single(notifier.Of(NotifyEvent.Success));
        Assert.Contains("รอแอดมินกลุ่มอนุมัติ", pending.Text);
    }

    [Fact]
    public async Task A_lost_login_and_a_facebook_block_are_block_messages()
    {
        var (shop, notifier, host) = await StartAsync();
        using var _ = shop;
        await using var __ = host;

        await RunAsync(shop, 0, ok: false, blocked: true);
        shop.Wait(TimeSpan.FromHours(50));
        await RunAsync(shop, 1, ok: false, needsLogin: true);

        Assert.Empty(notifier.Of(NotifyEvent.Fail)); // they are not ordinary failures
        var messages = notifier.Of(NotifyEvent.Block);
        Assert.Equal(2, messages.Count);
        Assert.Contains("Facebook ขัดขวางการโพสต์", messages[0].Text);
        Assert.Contains("ระบบพักเครื่องนี้", messages[0].Text);
        Assert.Contains("ต้องเข้าสู่ระบบใหม่", messages[1].Text);
        Assert.Contains("Shop PC", messages[1].Text);
    }

    [Fact]
    public async Task A_group_switched_off_after_repeated_failures_is_announced_by_name()
    {
        var (shop, notifier, host) = await StartAsync();
        using var _ = shop;
        await using var __ = host;

        for (var i = 0; i < 3; i++) await RunAsync(shop, 0, ok: false);

        var fails = notifier.Of(NotifyEvent.Fail);
        Assert.Equal(4, fails.Count); // three failures and the switch-off
        var off = fails[^1];
        Assert.Contains("ปิดกลุ่ม กลุ่ม 0 อัตโนมัติ", off.Text);
        Assert.Contains("3 ครั้ง", off.Text);
        Assert.Equal((shop.Set.Id, shop.Link(0).Id), (off.LinkSetId, off.LinkId));
    }

    [Fact]
    public async Task A_run_of_quota_failures_in_one_claim_is_one_message()
    {
        var (shop, notifier, host) = await StartAsync(links: 1);
        using var _ = shop;
        await using var __ = host;
        await shop.SetAdvancedAsync(a => a with { DailyAll = 1 });
        await RunAsync(shop, 0);
        for (var i = 0; i < 3; i++) await shop.TestPostAsync(0);

        Assert.Null(await shop.ClaimAsync());

        var quota = Assert.Single(notifier.Of(NotifyEvent.Quota));
        Assert.Contains("ครบโควตา", quota.Text);
        Assert.Contains("3 รายการ", quota.Text);
        Assert.Equal(shop.Ws, quota.WorkspaceId);
        Assert.Equal(3, (await shop.PostsAsync()).Count(p => p.FailureCode == FailureCode.Quota));
    }

    [Fact]
    public async Task A_round_is_summed_up_once_when_its_last_post_is_done()
    {
        var (shop, notifier, host) = await StartAsync(links: 2);
        using var _ = shop;
        await using var __ = host;
        await DueScheduleAsync(shop, 2);

        Assert.NotNull(await shop.RunNextAsync(ok: true));
        Assert.Empty(notifier.Of(NotifyEvent.Round)); // one post of the round is still to go
        shop.Wait(TimeSpan.FromMinutes(5));
        Assert.NotNull(await shop.RunNextAsync(ok: false, error: "ไม่ผ่าน"));

        var round = Assert.Single(notifier.Of(NotifyEvent.Round));
        Assert.Contains("จบรอบโพสต์", round.Text);
        Assert.Contains("ตารางเช้า", round.Text);
        Assert.Contains("สำเร็จ 1", round.Text);
        Assert.Contains("ล้มเหลว 1", round.Text);
        Assert.Contains("จากทั้งหมด 2", round.Text);
        Assert.Equal((shop.Ws, shop.Set.Id, null), (round.WorkspaceId, round.LinkSetId, round.LinkId));
    }

    [Fact]
    public async Task A_round_that_ends_with_a_skipped_post_is_summed_up_at_the_claim_that_skipped_it()
    {
        var (shop, notifier, host) = await StartAsync(links: 2);
        using var _ = shop;
        await using var __ = host;
        await shop.SetAntiBanAsync(a => a with { Shuffle = false }); // the groups of a round go in the set's order, so the first post is the one that goes out
        await DueScheduleAsync(shop, 2);
        await shop.UpdateLinkAsync(1, enabled: false);

        Assert.NotNull(await shop.RunNextAsync(ok: true));
        Assert.Empty(notifier.Of(NotifyEvent.Round));
        shop.Wait(TimeSpan.FromMinutes(5));
        Assert.Null(await shop.ClaimAsync()); // the second one is skipped: its group is off

        var round = Assert.Single(notifier.Of(NotifyEvent.Round));
        Assert.Contains("สำเร็จ 1", round.Text);
        Assert.Contains("ข้าม 1", round.Text);
    }

    [Fact]
    public async Task A_notification_that_fails_never_fails_the_claim_or_the_result()
    {
        var host = HostWith(new ThrowingNotifier());
        await using var _ = host;
        using var shop = await host.ShopAsync(factory.Clock, links: 2);
        await shop.SetAdvancedAsync(a => a with { DailyAll = 2 });

        var sent = await RunAsync(shop, 0);
        var failed = await RunAsync(shop, 1, ok: false); // a failure message that fails to go
        await RunAsync(shop, 1);
        await shop.TestPostAsync(0);
        Assert.Null(await shop.ClaimAsync()); // quota failure: a message that fails to go

        Assert.Equal((PostStatus.Success, PostStatus.Failed), (sent.Status, failed.Status));
        Assert.Equal(PostStatus.Failed, (await shop.PostsAsync()).Single(p => p.IsTest && p.FailureCode == FailureCode.Quota).Status);
    }
}
