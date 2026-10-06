using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// What the engine tells Telegram about the jobs themselves: which machine takes which job, a job that is lost or too late,
// a machine or a schedule that stops, a machine that goes silent while posts wait for it.
[Collection(ApiCollection.Name)]
public class JobNotificationTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private WebApplicationFactory<Program> HostWith(INotificationDispatcher notifier) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<INotificationDispatcher>();
            s.AddSingleton(notifier);
        }));

    private static async Task SaveSettingsAsync(Shop shop, bool job) =>
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/notifications",
            NotificationsEndpointsTests.Settings("123:tg", "-100123", tgOn: true, events: NotificationsEndpointsTests.Events(success: true, quota: true, job: job)), Json))
            .EnsureSuccessStatusCode();

    private async Task<(Shop Shop, RecordingNotifier Notifier, WebApplicationFactory<Program> Host)> StartAsync(
        string plan = "pro", int links = 2, int posts = 2, bool job = true)
    {
        var notifier = new RecordingNotifier();
        var host = HostWith(notifier);
        var shop = await host.ShopAsync(factory.Clock, plan, links, posts);
        await SaveSettingsAsync(shop, job);
        notifier.Messages.Clear(); // what setting the shop up said (the machine was paired before the messages were switched on)
        return (shop, notifier, host);
    }

    /// <summary>Two posts due at 10:00 tomorrow in a schedule, and the clock there.</summary>
    private static async Task<ScheduleCreatedDto> DueScheduleAsync(Shop shop)
    {
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/engine/offline", new { policy = "queue", window = "day", line = true, email = true, push = false }, Json)).EnsureSuccessStatusCode();
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: ToLocalDay(shop.Now.AddDays(1), 0), OnceTime: "10:00", Offset: 0, Name: "ตารางเช้า"));
        shop.WaitUntil(created.LastAt!.Value);
        return created;
    }

    private static string[] Lines(Sent message) => message.Text.Split('\n');

    // ---- the machine takes a job ----

    [Fact]
    public async Task A_job_taken_says_which_machine_posts_to_which_group_where_it_stands_and_only_when_asked_for()
    {
        var (shop, notifier, host) = await StartAsync();
        using var _ = shop;
        await using var __ = host;
        await DueScheduleAsync(shop);

        var job = (await shop.ClaimAsync())!;

        var taken = Lines(Assert.Single(notifier.Of(NotifyEvent.Job)));
        Assert.Equal(5, taken.Length);
        Assert.Equal("🚀 <b>เครื่อง Shop PC เริ่มโพสต์</b>", taken[0]);
        Assert.StartsWith("<b>" + job.GroupName, taken[1]);
        Assert.Equal(job.GroupUrl, taken[2]);
        Assert.Equal("ชุด กลุ่มขายของ · ตารางเช้า · รอบ 1 (กลุ่ม 1/2)", taken[3]);
        Assert.Matches(@"^🕒 \d\d/\d\d \d\d:\d\d:\d\d$", taken[4]);

        // Switched off (the default: it is one message per post), nothing is said about the next job.
        await shop.ReportAsync(job.PostId);
        shop.Wait(TimeSpan.FromMinutes(5));
        await SaveSettingsAsync(shop, job: false);
        Assert.NotNull(await shop.ClaimAsync());
        Assert.Single(notifier.Of(NotifyEvent.Job));
    }

    [Fact]
    public async Task A_job_the_machine_took_and_never_reported_is_announced_as_lost_when_it_asks_again()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        await shop.TestPostAsync(0);
        Assert.NotNull(await shop.ClaimAsync()); // taken, and the browser never says how it went
        shop.Wait(TimeSpan.FromMinutes(16));

        Assert.Null(await shop.ClaimAsync());

        var lost = Assert.Single(notifier.Of(NotifyEvent.Fail));
        Assert.StartsWith("⌛ <b>ทดสอบ · เครื่อง Shop PC ไม่ส่งผลการโพสต์กลับมา</b>", lost.Text);
        Assert.Contains("ภายใน 15 นาที", lost.Text);
        Assert.Contains("ชุด กลุ่มขายของ", lost.Text);
        Assert.Equal(shop.Link(0).Id, lost.LinkId);
    }

    [Fact]
    public async Task Posts_that_waited_too_long_for_the_machine_are_announced_once_as_skipped()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: ToLocalDay(shop.Now.AddDays(1), 0), OnceTime: "10:00", Offset: 0, Name: "ตารางเช้า"));
        shop.WaitUntil(created.LastAt!.Value + TimeSpan.FromHours(3)); // the default window is 2 hours

        Assert.Null(await shop.ClaimAsync());

        var late = Assert.Single(notifier.Of(NotifyEvent.Offline));
        Assert.StartsWith("⏭ <b>ข้ามโพสต์ที่ช้าเกินไป 2 รายการ</b>", late.Text);
        Assert.Contains("เครื่อง Shop PC", late.Text);
        Assert.Null(await shop.ClaimAsync());
        Assert.Single(notifier.Of(NotifyEvent.Offline)); // the posts are settled: nothing more to say
    }

    // ---- a machine stops and starts ----

    [Fact]
    public async Task Pausing_and_resuming_a_machine_from_the_web_is_announced_with_the_jobs_that_wait()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        var device = await shop.DeviceAsync();
        await shop.TestPostAsync(0);

        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/devices/{device.Id}", new { jobsPaused = true }, Json)).EnsureSuccessStatusCode();
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/devices/{device.Id}", new { jobsPaused = true }, Json)).EnsureSuccessStatusCode(); // no change: no message
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/devices/{device.Id}", new { jobsPaused = false }, Json)).EnsureSuccessStatusCode();

        var messages = notifier.Of(NotifyEvent.StartStop);
        Assert.Equal(2, messages.Count);
        Assert.Equal("⏸ <b>หยุดรับงานเครื่อง Shop PC</b>", Lines(messages[0])[0]);
        Assert.Contains("งานที่รออยู่ 1 รายการ", messages[0].Text);
        Assert.Equal("▶️ <b>เปิดรับงานเครื่อง Shop PC</b>", Lines(messages[1])[0]);
    }

    [Fact]
    public async Task A_rest_the_engine_gave_the_machine_is_announced_when_it_is_over()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        await shop.TestPostAsync(0);
        var job = (await shop.ClaimAsync())!;
        await shop.ReportAsync(job.PostId, ok: false, blocked: true, error: "Facebook จำกัดการโพสต์");
        Assert.True((await shop.HeartbeatAsync()).JobsPaused); // resting
        Assert.DoesNotContain(notifier.Of(NotifyEvent.StartStop), m => m.Text.Contains("พ้นช่วงพัก"));

        shop.Wait(TimeSpan.FromHours(50)); // a Facebook block rests the machine 24 to 48 hours
        Assert.False((await shop.HeartbeatAsync()).JobsPaused);
        await shop.HeartbeatAsync();

        var over = Assert.Single(notifier.Of(NotifyEvent.StartStop), m => m.Text.Contains("พ้นช่วงพัก"));
        Assert.Equal("▶️ <b>เครื่อง Shop PC พ้นช่วงพักแล้ว</b>", Lines(over)[0]);
        Assert.Contains("ที่พักเพราะ: Facebook ขัดขวางการโพสต์", over.Text);
    }

    [Fact]
    public async Task A_new_machine_and_an_unbound_one_are_announced()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;

        var code = await (await shop.Owner.PostAsync($"{shop.Api}/devices/pairing", null)).ReadAsync<PairingCodeDto>();
        var other = host.CreateClient();
        var pair = await (await other.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "Laptop", browser = "Chrome", version = "2.3.1" }, Json))
            .ReadAsync<PairResultDto>();
        await shop.TestPostAsync(0);
        (await shop.Owner.DeleteAsync($"{shop.Api}/devices/{pair.DeviceId}")).EnsureSuccessStatusCode();

        var paired = Assert.Single(notifier.Of(NotifyEvent.StartStop), m => m.Text.Contains("เชื่อมต่อแล้ว"));
        Assert.Equal("🔗 <b>เครื่องใหม่เชื่อมต่อแล้ว: Laptop</b>", Lines(paired)[0]);
        var unbound = Assert.Single(notifier.Of(NotifyEvent.StartStop), m => m.Text.Contains("ยกเลิกการผูก"));
        Assert.Equal("🔌 <b>ยกเลิกการผูกเครื่อง Laptop</b>", Lines(unbound)[0]);
    }

    // ---- a schedule stops and starts ----

    [Fact]
    public async Task Pausing_resuming_and_deleting_a_schedule_are_announced()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "daily", Times: ["09:00"], Offset: 0, Name: "ตารางเช้า"));
        var id = created.Schedule.Id;

        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/schedules/{id}/active", new { active = false }, Json)).EnsureSuccessStatusCode();
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/schedules/{id}/active", new { active = true }, Json)).EnsureSuccessStatusCode();
        (await shop.Owner.DeleteAsync($"{shop.Api}/schedules/{id}")).EnsureSuccessStatusCode();

        var messages = notifier.Of(NotifyEvent.StartStop);
        Assert.Equal(3, messages.Count);
        Assert.Equal("⏸ <b>หยุดตาราง “ตารางเช้า”</b>", Lines(messages[0])[0]);
        Assert.Matches(@"ลบคิวที่ยังไม่ได้โพสต์ \d+ รายการ", messages[0].Text);
        Assert.Equal("▶️ <b>เปิดตาราง “ตารางเช้า” ต่อ</b>", Lines(messages[1])[0]);
        Assert.Equal("🗑 <b>ลบตาราง “ตารางเช้า”</b>", Lines(messages[2])[0]);
        Assert.All(messages, m => Assert.Equal(shop.Set.Id, m.LinkSetId));
    }

    [Fact]
    public async Task A_once_only_schedule_that_has_run_its_day_says_it_is_done()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        await DueScheduleAsync(shop);
        shop.Wait(TimeSpan.FromDays(2));

        await shop.ClaimAsync(); // the top-up that runs first in a claim switches the schedule off

        var done = Assert.Single(notifier.Of(NotifyEvent.StartStop));
        Assert.Equal("🏁 <b>ตาราง “ตารางเช้า” ทำงานครบแล้ว</b>", Lines(done)[0]);
    }

    // ---- bumps ----

    [Fact]
    public async Task A_bump_taken_and_its_result_are_announced()
    {
        var (shop, notifier, host) = await StartAsync("agency", links: 1, posts: 1);
        using var _ = shop;
        await using var __ = host;
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(
            Mode: "once", StartNow: true, BumpHours: 2, Name: "ตารางดัน",
            Bump: new { rounds = 2, text = "ดันค่ะ", mediaIds = Array.Empty<Guid>(), imagesEach = 0 }));
        shop.WaitUntil(created.LastAt!.Value);
        var post = (await shop.ClaimAsync())!;
        const string postUrl = "https://www.facebook.com/groups/shop0/posts/123456789/";
        await shop.ReportAsync(post.PostId, postUrl: postUrl);
        shop.Wait(TimeSpan.FromHours(2.5));

        var bump = (await shop.ClaimAsync())!;
        await shop.ReportBumpAsync(bump.PostId);
        shop.Wait(TimeSpan.FromHours(2));
        var second = (await shop.ClaimAsync())!;
        await shop.ReportBumpAsync(second.PostId, ok: false, error: "ไม่พบช่องคอมเมนต์");

        var jobs = notifier.Of(NotifyEvent.Job);
        Assert.Equal(
            ["🚀 <b>เครื่อง Shop PC เริ่มโพสต์</b>", "💬 <b>เครื่อง Shop PC เริ่มดันโพสต์</b>", "✅ <b>ดันโพสต์แล้ว</b>", "💬 <b>เครื่อง Shop PC เริ่มดันโพสต์</b>"],
            jobs.Select(m => Lines(m)[0]));
        Assert.Contains(postUrl, jobs[1].Text);
        Assert.Contains("ตาราง “ตารางดัน”", jobs[1].Text);
        var failed = Assert.Single(notifier.Of(NotifyEvent.Fail), m => m.Text.StartsWith("❌ <b>ดันโพสต์ไม่สำเร็จ</b>"));
        Assert.Contains("สาเหตุ: ไม่พบช่องคอมเมนต์", failed.Text);
    }

    // ---- a machine goes silent while posts wait for it ----

    private async Task<int> CheckAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<CheckStalledDevicesCommand, int>>()
            .HandleAsync(new CheckStalledDevicesCommand());
    }

    [Fact]
    public async Task A_machine_that_goes_silent_with_posts_due_is_announced_once_and_again_when_it_is_back()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        List<Sent> Mine(string text) => notifier.Of(NotifyEvent.Offline).Where(m => m.WorkspaceId == shop.Ws && m.Text.Contains(text)).ToList();

        // A machine that is off with nothing to do is not a stall.
        shop.Wait(TimeSpan.FromMinutes(6));
        await CheckAsync(host);
        Assert.Empty(Mine("เครื่อง Shop PC"));

        await shop.TestPostAsync(0);
        await CheckAsync(host);
        Assert.Empty(Mine("เครื่อง Shop PC")); // due for no time yet
        shop.Wait(TimeSpan.FromMinutes(6));
        await CheckAsync(host);

        var stalled = Lines(Assert.Single(Mine("ออฟไลน์")));
        Assert.Equal("📴 <b>เครื่อง Shop PC ออฟไลน์ งานค้างรอ</b>", stalled[0]);
        Assert.Matches(@"^ไม่มีสัญญาณมา \d+ นาที \(ล่าสุด \d\d/\d\d \d\d:\d\d:\d\d\)$", stalled[1]);
        Assert.Equal("โพสต์ที่ถึงเวลาแล้วแต่ยังไม่ได้โพสต์ 1 รายการ", stalled[2]);
        Assert.Matches(@"^เก่าสุดครบกำหนด \d\d/\d\d \d\d:\d\d:\d\d$", stalled[3]);

        await CheckAsync(host);
        Assert.Single(Mine("ออฟไลน์")); // one stall is one message

        await shop.HeartbeatAsync();
        await CheckAsync(host);
        var back = Assert.Single(Mine("กลับมาออนไลน์"));
        Assert.Equal("📶 <b>เครื่อง Shop PC กลับมาออนไลน์</b>", Lines(back)[0]);
        await CheckAsync(host);
        Assert.Single(Mine("กลับมาออนไลน์"));
    }

    [Fact]
    public async Task A_machine_the_web_paused_is_not_a_stall()
    {
        var (shop, notifier, host) = await StartAsync(job: false);
        using var _ = shop;
        await using var __ = host;
        var device = await shop.DeviceAsync();
        await shop.TestPostAsync(0);
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/devices/{device.Id}", new { jobsPaused = true }, Json)).EnsureSuccessStatusCode();
        shop.Wait(TimeSpan.FromMinutes(12));

        await CheckAsync(host);

        Assert.DoesNotContain(notifier.Of(NotifyEvent.Offline), m => m.WorkspaceId == shop.Ws);
    }
}
