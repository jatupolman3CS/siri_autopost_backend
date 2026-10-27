using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class LineChatEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = ApiFactory.Json;
    private static readonly LineOptionsDto AlwaysAllowed = new("00:00", "23:59", 30, SplitBubbles: true, ImagesFirst: false);

    [Fact]
    public async Task Heartbeat_creates_line_account_under_concurrency()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);

        const string botId = "@concur_oa";
        const string oaName = "Concurrent OA";

        // Send 5 concurrent heartbeats with line info
        var tasks = Enumerable.Range(0, 5).Select(_ =>
            device.PostAsJsonAsync("/api/device/heartbeat", new
            {
                version = "2.5.0",
                sessionOk = true,
                line = new { ready = true, botId, oaName },
                lineSessionOk = true
            }, Json)
        ).ToArray();

        var responses = await Task.WhenAll(tasks);
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var status = (await r.Content.ReadFromJsonAsync<DeviceStatusDto>(Json))!;
            Assert.NotNull(status.LineAccountId);
            Assert.Null(status.LineNote);
        }

        // Verify in DB that exactly 1 LINE account exists with this handle
        var lineAccounts = await factory.WithDbAsync(db =>
            db.Accounts.Where(a => a.WorkspaceId == ws && a.Platform == Platform.Line && a.Handle == botId).ToListAsync());

        Assert.Single(lineAccounts);
        Assert.Equal(Platform.Line, lineAccounts[0].Platform);
        Assert.Contains(oaName, lineAccounts[0].Name);

        // Verify GET devices returns Line summary
        var devices = (await owner.GetFromJsonAsync<List<DeviceDto>>($"/api/workspaces/{ws}/devices", Json))!;
        var dev = devices.Single();
        Assert.NotNull(dev.Line);
        Assert.Equal(botId, dev.Line.BotId);
        Assert.Contains(oaName, dev.Line.Name);
    }

    [Fact]
    public async Task Put_line_chats_upserts_complete_replaces_and_preserves_doNotSend()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);

        const string botId = "@sync_oa";
        const string oaName = "Sync OA";

        // First heartbeat to link the account
        (await device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = true, botId, oaName }
        }, Json)).EnsureSuccessStatusCode();

        // 1. Initial sync with 2 chats
        var sync1 = await device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId,
            oaName,
            complete = false,
            chats = new[]
            {
                new { userId = "U11111111111111111111111111111111", name = "สมชาย", unread = true },
                new { userId = "U22222222222222222222222222222222", name = "สมหญิง", unread = false }
            }
        }, Json);
        Assert.Equal(HttpStatusCode.OK, sync1.StatusCode);

        // Fetch chats from web API
        var chats1 = (await owner.GetFromJsonAsync<LineChatPageDto>($"/api/workspaces/{ws}/line/chats", Json))!;
        Assert.Equal(2, chats1.Items.Count);

        var chat1 = chats1.Items.Single(c => c.Name == "สมชาย");

        // 2. User marks U111 as doNotSend
        var updateRes = await owner.PutAsJsonAsync($"/api/workspaces/{ws}/line/chats/{chat1.Id}", new
        {
            doNotSend = true
        }, Json);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updatedChat = (await updateRes.Content.ReadFromJsonAsync<LineChatDto>(Json))!;
        Assert.True(updatedChat.DoNotSend);

        // 3. Second sync with complete = true (omits U222, updates U111 name, adds U333)
        var sync2 = await device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId,
            oaName,
            complete = true,
            chats = new[]
            {
                new { userId = "U11111111111111111111111111111111", name = "สมชาย (เปลี่ยนชื่อ)", unread = false },
                new { userId = "U33333333333333333333333333333333", name = "สมศักดิ์", unread = true }
            }
        }, Json);
        Assert.Equal(HttpStatusCode.OK, sync2.StatusCode);

        // Fetch chats again
        var chats2 = (await owner.GetFromJsonAsync<LineChatPageDto>($"/api/workspaces/{ws}/line/chats", Json))!;
        Assert.Equal(2, chats2.Items.Count);

        // U222 was removed because complete = true
        Assert.DoesNotContain(chats2.Items, c => c.Name == "สมหญิง");

        // U111 survives and keeps DoNotSend == true
        var keptChat = chats2.Items.Single(c => c.Url.Contains("U11111111111111111111111111111111"));
        Assert.Equal("สมชาย (เปลี่ยนชื่อ)", keptChat.Name);
        Assert.True(keptChat.DoNotSend);

        // U333 was added
        Assert.Contains(chats2.Items, c => c.Name == "สมศักดิ์");
    }

    [Fact]
    public async Task Plan_gating_free_plan_cannot_use_line_features()
    {
        var (owner, _, ws) = await factory.SignUpAsync("free");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);

        // 1. Device PUT line/chats -> 403
        var putRes = await device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = "@free_oa",
            oaName = "Free OA",
            complete = true,
            chats = Array.Empty<object>()
        }, Json);
        Assert.Equal(HttpStatusCode.Forbidden, putRes.StatusCode);

        // 2. Owner POST link-sets with channel = "line" -> 403
        var setRes = await owner.PostAsJsonAsync($"/api/workspaces/{ws}/link-sets", new
        {
            name = "ชุดแชท",
            channel = "line"
        }, Json);
        Assert.Equal(HttpStatusCode.Forbidden, setRes.StatusCode);

        // 3. Owner GET line/chats -> 403
        var getRes = await owner.GetAsync($"/api/workspaces/{ws}/line/chats");
        Assert.Equal(HttpStatusCode.Forbidden, getRes.StatusCode);
    }

    [Fact]
    public async Task Import_from_chats_creates_links_in_line_set()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@import_oa");
        var (owner, ws, device) = (shop.Owner, shop.Ws, shop.Device);

        // Sync 3 chats
        await device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = "@import_oa",
            oaName = "Import OA",
            complete = true,
            chats = new[]
            {
                new { userId = "U10000000000000000000000000000001", name = "ลูกค้า 1", unread = false },
                new { userId = "U10000000000000000000000000000002", name = "ลูกค้า 2", unread = false },
                new { userId = "U10000000000000000000000000000003", name = "ลูกค้า 3", unread = false }
            }
        }, Json);

        var chats = (await owner.GetFromJsonAsync<LineChatPageDto>($"/api/workspaces/{ws}/line/chats", Json))!;
        var chat2 = chats.Items.Single(c => c.Name == "ลูกค้า 2");

        // Mark chat 2 as doNotSend
        await owner.PutAsJsonAsync($"/api/workspaces/{ws}/line/chats/{chat2.Id}", new { doNotSend = true }, Json);

        var accounts = (await owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        var lineAccount = accounts.Single(a => a.Platform == Platform.Line);

        // Import chats into shop.Set
        var importRes = await owner.PostAsJsonAsync(
            $"/api/workspaces/{ws}/link-sets/{shop.Set.Id}/links/from-line-chats",
            new { accountId = lineAccount.Id, allMatching = true },
            Json);
        Assert.Equal(HttpStatusCode.OK, importRes.StatusCode);
        var result = (await importRes.Content.ReadFromJsonAsync<BulkLinksResultDto>(Json))!;

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.SkippedDoNotSend);

        // Verify link set now has 2 links (ลูกค้า 1 and ลูกค้า 3)
        await shop.RefreshSetAsync();
        Assert.Equal(2, shop.Set.Links.Count);
        Assert.Contains(shop.Set.Links, l => l.Name == "ลูกค้า 1");
        Assert.Contains(shop.Set.Links, l => l.Name == "ลูกค้า 3");
    }

    [Fact]
    public async Task Claim_hands_line_job_only_to_line_ready_device_within_allowed_window()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@claim_oa");
        var (owner, ws, device) = (shop.Owner, shop.Ws, shop.Device);

        // Create schedule due now (startNow: true) with splitBubbles = true and AlwaysAllowed
        await shop.CreateScheduleAsync(new ScheduleSpec(
            Times: ["10:00"],
            StartNow: true,
            LineOptions: AlwaysAllowed));

        // 1. If device heartbeats with ready = false, claim returns null (no job)
        await device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = false, botId = "@claim_oa", oaName = "Claim OA" }
        }, Json);

        var claimNoReady = await device.PostAsync("/api/device/jobs/claim", null);
        Assert.Equal(HttpStatusCode.NoContent, claimNoReady.StatusCode);

        // 2. Set device ready = true
        await device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = true, botId = "@claim_oa", oaName = "Claim OA" }
        }, Json);

        // 3. Current time is within window - wait 30s so startNow post becomes due
        shop.Wait(TimeSpan.FromSeconds(30));
        var job = await shop.ClaimAsync();
        Assert.NotNull(job);
        Assert.Equal("line", job.Channel);
        Assert.False(job.Shot);
        Assert.NotNull(job.Line);
        Assert.NotNull(job.Bubbles);
        Assert.Equal(shop.Set.Links[0].Url, job.GroupUrl);

        // Report job completed
        await shop.ReportAsync(job.PostId, ok: true);

        // 4. In contrast, create a FB link set, post, and claim to compare shape
        var fbSet = await owner.CreateLinkSetAsync(ws, "ชุดเฟซ", shop.Pair.AccountId, channel: "fb");
        await owner.AddLinkAsync(ws, fbSet.Id, "https://www.facebook.com/groups/fbgroup1");
        await shop.CreateScheduleAsync(new ScheduleSpec(
            Times: ["10:00"],
            StartNow: true),
            set: fbSet.Id);

        shop.Wait(TimeSpan.FromSeconds(30));
        var fbJob = await shop.ClaimAsync();
        Assert.NotNull(fbJob);
        Assert.Equal("fb", fbJob.Channel);
        Assert.Null(fbJob.Bubbles);
        Assert.Null(fbJob.Line);
        await shop.ReportAsync(fbJob.PostId, ok: true);
    }

    [Fact]
    public async Task Result_path_skip_disables_link_skips_post_no_streak()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@skip_oa");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true, LineOptions: AlwaysAllowed));

        shop.Wait(TimeSpan.FromSeconds(30));
        var job = await shop.ClaimAsync();
        Assert.NotNull(job);

        // Report skip
        var post = await shop.ReportAsync(job.PostId, ok: false, skip: true, reason: "ลูกค้าบล็อค");
        Assert.Equal(PostStatus.Skipped, post.Status);

        // Link is disabled manually and health is Off
        var link = await shop.LinkNowAsync(0);
        Assert.False(link.Enabled);
        Assert.Equal(LinkHealth.Off, link.Health);
        Assert.Equal(0, link.FailStreak);
    }

    [Fact]
    public async Task Result_path_deferMinutes_requeues_later_and_caps_at_six_defers()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@defer_oa");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true, LineOptions: AlwaysAllowed));

        shop.Wait(TimeSpan.FromSeconds(30));
        // First defer
        var job1 = await shop.ClaimAsync();
        Assert.NotNull(job1);

        var post1 = await shop.ReportAsync(job1.PostId, ok: false, deferMinutes: 30, reason: "ลูกค้าเพิ่งตอบ");
        Assert.Equal(PostStatus.Queued, post1.Status);
        Assert.Equal(1, post1.DeferCount);
        Assert.InRange((post1.ScheduledAt - shop.Now).TotalMinutes, 23, 37);

        // Advance clock and simulate 5 more defers (total 6)
        for (var i = 2; i <= 6; i++)
        {
            shop.Wait(TimeSpan.FromMinutes(40));
            await shop.Device.PostAsJsonAsync("/api/device/heartbeat", new
            {
                version = "2.5.0",
                line = new { ready = true, botId = "@defer_oa", oaName = "Defer OA" }
            }, Json);
            var nextJob = await shop.ClaimAsync();
            Assert.NotNull(nextJob);
            var deferred = await shop.ReportAsync(nextJob.PostId, ok: false, deferMinutes: 30, reason: $"เลื่อนรอบที่ {i}");
            Assert.Equal(PostStatus.Queued, deferred.Status);
            Assert.Equal(i, deferred.DeferCount);
        }

        // 7th defer: exceeds max (>= 6), post is skipped!
        shop.Wait(TimeSpan.FromMinutes(40));
        await shop.Device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = true, botId = "@defer_oa", oaName = "Defer OA" }
        }, Json);
        var job7 = await shop.ClaimAsync();
        Assert.NotNull(job7);
        var skipped = await shop.ReportAsync(job7.PostId, ok: false, deferMinutes: 30, reason: "เลื่อนครั้งที่ 7");
        Assert.Equal(PostStatus.Skipped, skipped.Status);
    }

    [Fact]
    public async Task Result_path_needsLogin_marks_only_line_account_relogin()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@login_oa");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true, LineOptions: AlwaysAllowed));

        shop.Wait(TimeSpan.FromSeconds(30));
        var job = await shop.ClaimAsync();
        Assert.NotNull(job);

        await shop.ReportAsync(job.PostId, ok: false, needsLogin: true, error: "LINE OA เซสชันหลุด");

        var accounts = (await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{shop.Ws}/accounts", Json))!;
        var lineAccount = accounts.Single(a => a.Platform == Platform.Line);
        var fbAccount = accounts.Single(a => a.Platform == Platform.Fb);

        Assert.Equal(AccountHealth.Relogin, lineAccount.Health);
        Assert.Equal(AccountHealth.Ok, fbAccount.Health);
    }

    [Fact]
    public async Task Result_path_blocked_pauses_whole_device_with_line_thai_text()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@blocked_oa");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true, LineOptions: AlwaysAllowed));

        shop.Wait(TimeSpan.FromSeconds(30));
        var job = await shop.ClaimAsync();
        Assert.NotNull(job);

        await shop.ReportAsync(job.PostId, ok: false, blocked: true, error: "LINE แจ้งเตือนส่งข้อความถี่เกินไป");

        var device = await shop.DeviceAsync();
        Assert.True(device.AutoPausedUntil > shop.Now);

        var events = await shop.EventsAsync("device.updated", 0);
        var pauseEvent = Assert.Single(events, e => e.Payload.TryGetProperty("autoPauseReason", out _));
        Assert.Contains("LINE", pauseEvent.Payload.GetProperty("autoPauseReason").GetString()!);
    }

    [Fact]
    public async Task Line_failure_streak_rests_only_line_account_while_fb_still_gets_jobs()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 4, posts: 1, botId: "@streak_oa");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true, LineOptions: AlwaysAllowed));

        // Create an FB set and post too
        var fbSet = await shop.Owner.CreateLinkSetAsync(shop.Ws, "FB Set", shop.Pair.AccountId, channel: "fb");
        await shop.Owner.AddLinkAsync(shop.Ws, fbSet.Id, "https://www.facebook.com/groups/fbstreak");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true), set: fbSet.Id);

        // Fail 4 consecutive LINE jobs
        for (var i = 0; i < 4; i++)
        {
            shop.Wait(TimeSpan.FromMinutes(2));
            await shop.Device.PostAsJsonAsync("/api/device/heartbeat", new
            {
                version = "2.5.0",
                line = new { ready = true, botId = "@streak_oa", oaName = "Streak OA" }
            }, Json);
            var job = await shop.ClaimAsync();
            if (job?.Channel == "fb")
            {
                // report FB ok so we get to LINE jobs
                await shop.ReportAsync(job.PostId, ok: true);
                job = await shop.ClaimAsync();
            }
            Assert.NotNull(job);
            Assert.Equal("line", job.Channel);
            await shop.ReportAsync(job.PostId, ok: false, error: "ส่งแชทล้มเหลว");
        }

        // Verify LINE account is now in RestUntil via DB
        var accounts = (await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{shop.Ws}/accounts", Json))!;
        var lineAccount = accounts.Single(a => a.Platform == Platform.Line);
        var fbAccount = accounts.Single(a => a.Platform == Platform.Fb);

        var (lineRest, fbRest) = await factory.WithDbAsync(async db =>
        {
            var la = await db.Accounts.FindAsync(lineAccount.Id);
            var fa = await db.Accounts.FindAsync(fbAccount.Id);
            return (la?.RestUntil, fa?.RestUntil);
        });

        Assert.NotNull(lineRest);
        Assert.True(lineRest > shop.Now);
        Assert.Null(fbRest);

        // Device will not get LINE jobs while resting
        var noLineJob = await shop.ClaimAsync();
        Assert.True(noLineJob is null || noLineJob.Channel != "line");
    }

    [Fact]
    public async Task Second_device_cannot_take_oa_held_by_another_device()
    {
        var (owner, _, ws) = await factory.SignUpAsync("agency");
        var (device1, _) = await factory.PairDeviceAsync(owner, ws);
        var (device2, _) = await factory.PairDeviceAsync(owner, ws);

        const string botId = "@shared_oa";

        // Device 1 binds OA
        var hb1 = await device1.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = true, botId, oaName = "Shared OA" }
        }, Json);
        Assert.Equal(HttpStatusCode.OK, hb1.StatusCode);

        // Device 2 attempts to bind the same OA
        var hb2 = await device2.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = true, botId, oaName = "Shared OA" }
        }, Json);
        Assert.Equal(HttpStatusCode.OK, hb2.StatusCode);
        var status2 = (await hb2.Content.ReadFromJsonAsync<DeviceStatusDto>(Json))!;

        Assert.NotNull(status2.LineNote);
        Assert.Contains("บัญชี LINE OA นี้ผูกอยู่กับเครื่องอื่นในเวิร์กสเปซแล้ว", status2.LineNote);

        // Only 1 LINE account exists in workspace
        var accounts = (await owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        Assert.Single(accounts, a => a.Platform == Platform.Line);
    }

    [Fact]
    public async Task Put_device_returns_line_summary()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@rename_oa", oaName: "Rename OA");

        var devices = (await shop.Owner.GetFromJsonAsync<List<DeviceDto>>($"{shop.Api}/devices", Json))!;
        var dev = devices.Single();
        Assert.NotNull(dev.Line);
        Assert.Equal("@rename_oa", dev.Line.BotId);
        Assert.Contains("Rename OA", dev.Line.Name);
        Assert.True(dev.Line.Ready);

        // Rename device via PUT /api/workspaces/{ws}/devices/{id}
        var updateRes = await shop.Owner.PutAsJsonAsync($"{shop.Api}/devices/{dev.Id}", new
        {
            name = "Office Machine Renamed"
        }, Json);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = (await updateRes.Content.ReadFromJsonAsync<DeviceDto>(Json))!;

        Assert.Equal("Office Machine Renamed", updated.Name);
        Assert.NotNull(updated.Line);
        Assert.Equal("@rename_oa", updated.Line.BotId);
        Assert.True(updated.Line.Ready);
    }

    [Fact]
    public async Task Reports_and_public_share_snapshot_contain_no_line_rows()
    {
        using var shop = await factory.PairedLineShopAsync("agency", links: 1, posts: 1, botId: "@report_oa");
        var (owner, ws) = (shop.Owner, shop.Ws);

        // Complete a LINE job
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true, LineOptions: AlwaysAllowed));
        shop.Wait(TimeSpan.FromSeconds(30));
        var lineJob = await shop.ClaimAsync();
        Assert.NotNull(lineJob);
        await shop.ReportAsync(lineJob.PostId, ok: true);

        // Create FB link set and complete an FB job
        var fbSet = await owner.CreateLinkSetAsync(ws, "FB Set", shop.Pair.AccountId, channel: "fb");
        await owner.AddLinkAsync(ws, fbSet.Id, "https://www.facebook.com/groups/fbreportgroup");
        await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["10:00"], StartNow: true), set: fbSet.Id);
        shop.Wait(TimeSpan.FromSeconds(30));
        var fbJob = await shop.ClaimAsync();
        Assert.NotNull(fbJob);
        await shop.ReportAsync(fbJob.PostId, ok: true, postUrl: "https://www.facebook.com/groups/fbreportgroup/posts/123");

        // 1. Check workspace reports: only FB rows appear, 0 LINE rows
        var report = (await owner.GetFromJsonAsync<ReportDto>($"/api/workspaces/{ws}/reports", Json))!;
        Assert.DoesNotContain(report.Groups, g => g.Name.Contains("ลูกค้า"));

        // 2. Create public share link
        var shareRes = await owner.PostAsJsonAsync($"/api/workspaces/{ws}/reports/share", new
        {
            brand = "My Shop",
            period = "week",
            logo = true
        }, Json);
        Assert.Equal(HttpStatusCode.OK, shareRes.StatusCode);
        var shareDto = (await shareRes.Content.ReadFromJsonAsync<ReportShareDto>(Json))!;

        // 3. Open shared report as anonymous user
        var anon = factory.CreateClient();
        var sharedRes = await anon.GetAsync($"/api/reports/shared/{shareDto.Token}");
        Assert.Equal(HttpStatusCode.OK, sharedRes.StatusCode);
        var shared = (await sharedRes.Content.ReadFromJsonAsync<SharedReportDto>(Json))!;

        Assert.DoesNotContain(shared.Report.Groups, g => g.Name.Contains("ลูกค้า"));
    }

    [Fact]
    public async Task Revoke_device_removes_chats_and_line_account()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@revoke_oa");
        var (owner, ws, device) = (shop.Owner, shop.Ws, shop.Device);

        // Sync chats
        await device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = "@revoke_oa",
            oaName = "Revoke OA",
            complete = true,
            chats = new[]
            {
                new { userId = "U99999999999999999999999999999999", name = "ลูกค้ากำลังจะลบ", unread = false }
            }
        }, Json);

        var chatsBefore = (await owner.GetFromJsonAsync<LineChatPageDto>($"/api/workspaces/{ws}/line/chats", Json))!;
        Assert.Single(chatsBefore.Items);

        // Revoke device via DELETE /api/workspaces/{ws}/devices/{id}
        var delRes = await owner.DeleteAsync($"{shop.Api}/devices/{shop.Pair.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);

        // Chats are deleted
        var chatsAfter = (await owner.GetFromJsonAsync<LineChatPageDto>($"/api/workspaces/{ws}/line/chats", Json))!;
        Assert.Empty(chatsAfter.Items);
    }

    [Fact]
    public async Task Backup_round_trip_with_line_set()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 2, posts: 2, botId: "@backup_oa");
        var (owner, ws) = (shop.Owner, shop.Ws);

        // Create schedule with LineOptions on the line set
        var sched = await shop.CreateScheduleAsync(new ScheduleSpec(
            Times: ["11:00"],
            LineOptions: new LineOptionsDto("08:00", "21:00", 45, SplitBubbles: true, ImagesFirst: true)));

        // Export backup
        var backupRaw = await owner.GetStringAsync($"{shop.Api}/backup");
        var backupDto = JsonSerializer.Deserialize<BackupDto>(backupRaw, Json)!;

        // Verify backup contains the LINE set and schedule with LineOptions
        var lineSetBackup = Assert.Single(backupDto.LinkSets, s => s.Channel == Platform.Line);
        Assert.Equal(2, lineSetBackup.Links.Count);
        var lineSchedBackup = Assert.Single(backupDto.Schedules, s => s.LinkSet == lineSetBackup.Name);
        Assert.NotNull(lineSchedBackup.LineOptions);
        Assert.Equal("08:00", lineSchedBackup.LineOptions.AllowedFrom);
        Assert.True(lineSchedBackup.LineOptions.SplitBubbles);

        // Restore backup into a new shop
        using var restoreShop = await factory.ShopAsync("pro", links: 0, posts: 0);
        // Ensure new shop has line ready so Line sets and schedules can restore
        await restoreShop.Device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0",
            line = new { ready = true, botId = "@backup_oa", oaName = "Backup OA" }
        }, Json);

        var restoreRes = await restoreShop.Owner.PostAsJsonAsync($"{restoreShop.Api}/restore", backupDto, Json);
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);

        // Verify restored link set has Channel == Platform.Line and links
        var restoredSets = await restoreShop.Owner.LinkSetsAsync(restoreShop.Ws);
        var restoredLineSet = Assert.Single(restoredSets, s => s.Channel == Platform.Line);
        Assert.Equal(2, restoredLineSet.Links.Count);

        // Verify restored schedule has LineOptions
        var restoredScheds = await restoreShop.SchedulesAsync();
        var restoredSched = Assert.Single(restoredScheds, s => s.LinkSetId == restoredLineSet.Id);
        Assert.NotNull(restoredSched.LineOptions);
        Assert.Equal("08:00", restoredSched.LineOptions.AllowedFrom);
        Assert.True(restoredSched.LineOptions.SplitBubbles);
    }

    [Fact]
    public async Task Letting_go_of_the_line_oa_frees_the_browser_for_another_one()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@old_oa", oaName: "Old OA");
        var deviceId = (await shop.DeviceAsync()).Id;
        var oldAccount = (await shop.DeviceAsync()).Line!.AccountId;

        // Changing the OA by just reporting another one is refused: the browser is bound to the first.
        var refused = await shop.Device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0", sessionOk = true, line = new { ready = true, botId = "@new_oa", oaName = "New OA" }, lineSessionOk = true,
        }, Json);
        Assert.Contains("ไม่สามารถเปลี่ยน LINE OA ได้", (await refused.Content.ReadFromJsonAsync<DeviceStatusDto>(Json))!.LineNote);

        // The web lets go of it: no LINE on the device, the account stays but has no browser, the extension is told to forget it.
        var res = await shop.Owner.DeleteAsync($"{shop.Api}/devices/{deviceId}/line");
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<DeviceDto>(Json))!;
        Assert.Null(dto.Line);
        Assert.Null((await shop.DeviceAsync()).Line);
        var kept = await factory.WithDbAsync(db => db.Accounts.SingleAsync(a => a.Id == oldAccount));
        Assert.Null(kept.DeviceId);
        Assert.Equal(0, await factory.WithDbAsync(db => db.LineChats.CountAsync(c => c.AccountId == oldAccount)));
        Assert.Contains(
            await factory.WithDbAsync(db => db.DeviceCommands.Where(c => c.DeviceId == deviceId).Select(c => c.Cmd).ToListAsync()),
            c => c == "resetLine");

        // A second OA can now be connected on the same browser, as a new account; Facebook is untouched.
        var next = (await (await shop.Device.PostAsJsonAsync("/api/device/heartbeat", new
        {
            version = "2.5.0", sessionOk = true, line = new { ready = true, botId = "@new_oa", oaName = "New OA" }, lineSessionOk = true,
        }, Json)).Content.ReadFromJsonAsync<DeviceStatusDto>(Json))!;
        Assert.Null(next.LineNote);
        Assert.NotNull(next.LineAccountId);
        Assert.NotEqual(oldAccount, next.LineAccountId);
        var accounts = (await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"{shop.Api}/accounts", Json))!;
        Assert.Equal(2, accounts.Count(a => a.Platform == Platform.Line));
        Assert.Single(accounts, a => a.Platform == Platform.Fb && a.Connected);
    }

    [Fact]
    public async Task Only_an_admin_can_let_go_of_the_line_oa()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 1, posts: 1, botId: "@role_oa", oaName: "Role OA");
        var deviceId = (await shop.DeviceAsync()).Id;
        var (viewer, auth, _) = await factory.SignUpAsync();
        (await shop.Owner.PostAsJsonAsync($"{shop.Api}/members", new { email = auth.User.Email, role = "viewer" }, Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"{shop.Api}/devices/{deviceId}/line")).StatusCode);
        Assert.NotNull((await shop.DeviceAsync()).Line);
    }
}
