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

internal sealed record Sent(NotifyEvent Event, Guid WorkspaceId, Guid? LinkSetId, Guid? LinkId, string Text, byte[]? Photo = null);

/// <summary>Remembers what the engine asked to be sent.</summary>
internal sealed class RecordingNotifier : INotificationDispatcher
{
    public ConcurrentQueue<Sent> Messages { get; } = new();

    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default, byte[]? photo = null)
    {
        Messages.Enqueue(new Sent(ev, workspaceId, linkSetId, linkId, text, photo));
        return Task.CompletedTask;
    }

    public List<Sent> Of(NotifyEvent ev) => Messages.Where(m => m.Event == ev).ToList();
}

internal sealed class ThrowingNotifier : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default, byte[]? photo = null) =>
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
        var shop = await host.ShopAsync(factory.Clock, links: links, posts: posts);
        await EnableTelegramAsync(shop);
        notifier.Messages.Clear(); // what setting the shop up said (pairing the machine is announced too)
        return (shop, notifier, host);
    }

    // Telegram on with the messages these tests look at: the engine only works out a message's details (set, round, next
    // group) when somebody would get it, and asks the device for a screenshot only when one would go along.
    private static async Task EnableTelegramAsync(Shop shop, bool shot = true) =>
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/notifications",
            NotificationsEndpointsTests.Settings("123:tg", "-100123", tgOn: true, events: NotificationsEndpointsTests.Events(success: true, shot: shot, quota: true)), Json))
            .EnsureSuccessStatusCode();

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
        Assert.StartsWith("✅ <b>ทดสอบ · โพสต์สำเร็จ</b>", sent.Text); // the test post says so
        Assert.Contains("<b>กลุ่ม 1 (C1)</b>\n" + shop.Link(1).Url, sent.Text); // the group in bold, its address under it
        Assert.Contains("ชุด " + shop.Set.Name, sent.Text);
        Assert.Contains("🕒", sent.Text);
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
        Assert.Contains("❌ <b>ทดสอบ · โพสต์ไม่สำเร็จ ข้ามกลุ่มนี้</b>", failed.Text);
        Assert.Contains("สาเหตุ: โดนจำกัดการโพสต์", failed.Text);
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
        Assert.Contains("ปิดกลุ่มอัตโนมัติ", off.Text);
        Assert.Contains("กลุ่ม 0", off.Text);
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
        Assert.Contains("สรุปรอบ", round.Text);
        Assert.Contains("ตารางเช้า", round.Text);
        Assert.Contains("สำเร็จ 1 กลุ่ม", round.Text);
        Assert.Contains("ไม่สำเร็จ 1 กลุ่ม", round.Text);
        Assert.Contains("ไม่ผ่าน", round.Text); // the failed group is named with its reason
        Assert.Contains("ทั้งหมด 2 กลุ่ม", round.Text);
        Assert.Equal((shop.Ws, shop.Set.Id, null), (round.WorkspaceId, round.LinkSetId, round.LinkId));
    }

    [Fact]
    public async Task A_round_that_ends_with_a_skipped_post_is_summed_up_at_the_claim_that_skipped_it()
    {
        var (shop, notifier, host) = await StartAsync(links: 2);
        using var _ = shop;
        await using var __ = host;
        await shop.SetAntiBanAsync(a => a with { Shuffle = false }); // the groups go out in the set's order, so the switched-off one is the second
        await DueScheduleAsync(shop, 2);
        await shop.UpdateLinkAsync(1, enabled: false);

        Assert.NotNull(await shop.RunNextAsync(ok: true));
        Assert.Empty(notifier.Of(NotifyEvent.Round));
        shop.Wait(TimeSpan.FromMinutes(5));
        Assert.Null(await shop.ClaimAsync()); // the second one is skipped: its group is off

        var round = Assert.Single(notifier.Of(NotifyEvent.Round));
        Assert.Contains("สำเร็จ 1 กลุ่ม", round.Text);
        Assert.Contains("ข้าม 1 กลุ่ม", round.Text);
    }

    [Fact]
    public async Task A_message_in_a_round_names_the_set_the_schedule_the_round_the_place_in_it_and_the_next_group()
    {
        var (shop, notifier, host) = await StartAsync(links: 2);
        using var _ = shop;
        await using var __ = host;
        await DueScheduleAsync(shop, 2);

        Assert.NotNull(await shop.RunNextAsync(ok: false, error: "กดโพสต์แล้วแต่หน้าต่างไม่ปิด"));

        // The layout the extension has always sent to Telegram: headline, group, address, reason, where, what is next.
        var lines = Assert.Single(notifier.Of(NotifyEvent.Fail)).Text.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Equal("❌ <b>โพสต์ไม่สำเร็จ ข้ามกลุ่มนี้</b>", lines[0]);
        Assert.StartsWith("<b>กลุ่ม ", lines[1]);
        Assert.StartsWith("https://www.facebook.com/groups/", lines[2]);
        Assert.Equal("สาเหตุ: กดโพสต์แล้วแต่หน้าต่างไม่ปิด", lines[3]);
        Assert.Equal("ชุด กลุ่มขายของ · ตารางเช้า · รอบ 1 (กลุ่ม 1/2)", lines[4]);
        Assert.Matches(@"^⏭ กลุ่มถัดไป: \d\d/\d\d \d\d:\d\d:\d\d$", lines[5]);

        shop.Wait(TimeSpan.FromMinutes(5));
        Assert.NotNull(await shop.RunNextAsync(ok: true));
        var done = Assert.Single(notifier.Of(NotifyEvent.Success)).Text.Split('\n');
        Assert.Equal("✅ <b>โพสต์สำเร็จ</b>", done[0]);
        Assert.Equal("ชุด กลุ่มขายของ · ตารางเช้า · รอบ 1 (กลุ่ม 2/2)", done[3]);
        Assert.Matches(@"^🕒 \d\d/\d\d \d\d:\d\d:\d\d$", done[4]);
        Assert.Equal(5, done.Length); // the round is over and nothing else is queued: no "next" line
    }

    [Fact]
    public async Task Things_a_person_typed_are_escaped_so_they_cannot_break_the_message()
    {
        var (shop, notifier, host) = await StartAsync();
        using var _ = shop;
        await using var __ = host;

        await RunAsync(shop, 0, ok: false); // the error text is the test helper's
        await shop.TestPostAsync(0);
        var job = await shop.ClaimAsync();
        await shop.ReportAsync(job!.PostId, ok: false, error: "<script>x</script> & ผิดพลาด");

        var text = notifier.Of(NotifyEvent.Fail).Last().Text;
        Assert.Contains("สาเหตุ: &lt;script&gt;x&lt;/script&gt; &amp; ผิดพลาด", text);
        Assert.DoesNotContain("<script>", text);
    }

    private static readonly byte[] SamplePicture = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03];

    private static string DataUrl(byte[] bytes, string type = "image/jpeg") => $"data:{type};base64,{Convert.ToBase64String(bytes)}";

    [Fact]
    public async Task A_job_asks_for_a_screenshot_only_when_a_telegram_message_with_one_would_go_and_the_picture_goes_with_the_message()
    {
        var (shop, notifier, host) = await StartAsync(links: 1);
        using var _ = shop;
        await using var __ = host;

        await shop.TestPostAsync(0);
        var job = await shop.ClaimAsync();
        Assert.True(job!.Shot);
        await shop.ReportAsync(job.PostId, ok: true, shot: DataUrl(SamplePicture));
        var posted = Assert.Single(notifier.Of(NotifyEvent.Success));
        Assert.Equal(SamplePicture, posted.Photo);

        // The screenshot event switched off: nobody would see a picture, so the device is not asked to take one.
        await EnableTelegramAsync(shop, shot: false);
        shop.Wait(TimeSpan.FromMinutes(5));
        await shop.TestPostAsync(0);
        var plain = await shop.ClaimAsync();
        Assert.False(plain!.Shot);
        await shop.ReportAsync(plain.PostId, ok: true, shot: DataUrl(SamplePicture)); // a picture sent anyway still only reaches the message
        Assert.Equal(2, notifier.Of(NotifyEvent.Success).Count);
    }

    [Fact]
    public async Task A_failure_carries_its_picture_too_and_something_that_is_not_a_picture_is_ignored()
    {
        var (shop, notifier, host) = await StartAsync(links: 1);
        using var _ = shop;
        await using var __ = host;

        await shop.TestPostAsync(0);
        var failing = await shop.ClaimAsync();
        await shop.ReportAsync(failing!.PostId, ok: false, error: "ผิดพลาด", shot: DataUrl(SamplePicture, "image/png"));
        Assert.Equal(SamplePicture, Assert.Single(notifier.Of(NotifyEvent.Fail)).Photo);

        shop.Wait(TimeSpan.FromMinutes(5));
        await shop.TestPostAsync(0);
        var junk = await shop.ClaimAsync();
        var result = await shop.ReportAsync(junk!.PostId, ok: true, shot: DataUrl("not a picture"u8.ToArray(), "text/plain"));
        Assert.Equal(PostStatus.Success, result.Status); // the result is what matters
        Assert.Null(notifier.Of(NotifyEvent.Success).Last().Photo);
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
