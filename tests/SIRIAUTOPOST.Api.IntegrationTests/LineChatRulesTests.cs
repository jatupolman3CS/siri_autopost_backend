using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

/// <summary>
/// The rules that keep a LINE channel safe and stable next to Facebook: quiet hours, the daily cap, "do not send",
/// taking an OA over, the roster's filters and its safety nets.
/// </summary>
[Collection(ApiCollection.Name)]
public class LineChatRulesTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = ApiFactory.Json;
    private static readonly LineOptionsDto AlwaysAllowed = new("00:00", "23:59", 30, SplitBubbles: false, ImagesFirst: false);

    private static string UserId(int i) => $"U{i:D32}";

    private static async Task SyncChatsAsync(Shop shop, params (int Index, string Name, string[]? Labels)[] chats)
    {
        var res = await shop.Device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = shop.LineBotId,
            oaName = shop.LineOaName,
            complete = false,
            chats = chats.Select(c => new { userId = UserId(c.Index), name = c.Name, labels = c.Labels ?? [], unread = false }).ToArray(),
        }, Json);
        res.EnsureSuccessStatusCode();
    }

    private static async Task<LineChatPageDto> RosterAsync(Shop shop, string query = "") =>
        (await shop.Owner.GetFromJsonAsync<LineChatPageDto>($"{shop.Api}/line/chats{query}", Json))!;

    [Fact]
    public async Task Quiet_hours_hold_a_post_until_the_window_opens_and_never_fail_or_skip_it()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 2, posts: 1, botId: "@quiet_oa");
        // A window that does not contain "now" (UTC: the schedule's offset is 0).
        var hour = shop.Now.Hour;
        var (from, to) = ($"{(hour + 3) % 24:D2}:00", $"{(hour + 5) % 24:D2}:00");
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartNow: true, LineOptions: new LineOptionsDto(from, to, 30, false, false)));

        shop.Wait(TimeSpan.FromSeconds(30));
        await shop.LineHeartbeatAsync();
        Assert.Null(await shop.ClaimAsync()); // outside the allowed hours nothing is handed out

        // "Post now" moves a post to this very moment, outside the hours: it still waits, it does not fail.
        var posts = await shop.PostsAsync(created.Schedule.Id);
        var target = posts.OrderBy(p => p.ScheduledAt).First();
        (await shop.Owner.PostAsync($"{shop.Api}/posts/{target.Id}/run-now", null)).EnsureSuccessStatusCode();

        // A long wait (past the 10-minute grace of the skip policy) inside the closed hours: held, not late, not skipped.
        shop.Wait(TimeSpan.FromMinutes(20));
        await shop.LineHeartbeatAsync();
        Assert.Null(await shop.ClaimAsync());
        Assert.Equal(PostStatus.Queued, (await shop.PostAsync(target.Id)).Status);

        // When the window opens the post goes out.
        var open = new DateTimeOffset(shop.Now.Year, shop.Now.Month, shop.Now.Day, 0, 0, 0, TimeSpan.Zero).AddHours((hour + 3) % 24);
        if (open <= shop.Now) open = open.AddDays(1);
        shop.WaitUntil(open.AddMinutes(20)); // the schedule's own posts start up to 15 minutes into the window
        await shop.LineHeartbeatAsync();
        var job = await shop.ClaimAsync();
        Assert.NotNull(job);
        Assert.Equal("line", job.Channel);
    }

    [Fact]
    public async Task A_line_post_over_the_daily_cap_waits_instead_of_failing()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 3, posts: 1, botId: "@cap_oa");
        var engine = (await shop.Owner.GetFromJsonAsync<EngineSettingsDto>($"{shop.Api}/engine", Json))!;
        var antiBan = engine.AntiBan with { Limits = engine.AntiBan.Limits with { Line = 1 }, Min = 1, Max = 2 };
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/engine/anti-ban", antiBan, Json)).EnsureSuccessStatusCode();
        await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartNow: true, LineOptions: AlwaysAllowed));

        shop.Wait(TimeSpan.FromSeconds(60));
        await shop.LineHeartbeatAsync();
        var first = await shop.ClaimAsync();
        Assert.NotNull(first);
        await shop.ReportAsync(first.PostId, ok: true);

        shop.Wait(TimeSpan.FromMinutes(2));
        await shop.LineHeartbeatAsync();
        Assert.Null(await shop.ClaimAsync()); // one chat a day is the cap: nothing more is handed out ...
        var states = (await shop.PostsAsync()).Where(p => p.Platform == Platform.Line).Select(p => p.Status).ToList();
        Assert.DoesNotContain(PostStatus.Failed, states); // ... and the others are NOT failed with a quota error
        Assert.Contains(PostStatus.Queued, states);
    }

    [Fact]
    public async Task A_chat_flagged_do_not_send_is_skipped_when_it_comes_due()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 2, posts: 1, botId: "@dns_oa");
        await SyncChatsAsync(shop, (0, "ลูกค้า 1", null), (1, "ลูกค้า 2", null));
        var roster = await RosterAsync(shop);
        var flagged = roster.Items.Single(c => c.Name == "ลูกค้า 1");
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/line/chats/{flagged.Id}", new { doNotSend = true }, Json)).EnsureSuccessStatusCode();
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartNow: true, LineOptions: AlwaysAllowed));

        // The opening round puts its chats in a random order, spaced by the LINE gap (45-120 s): claim a few times so both
        // chats come due, whichever is first.
        var handedOut = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            shop.Wait(TimeSpan.FromMinutes(3));
            await shop.LineHeartbeatAsync();
            var job = await shop.ClaimAsync();
            if (job is null) continue;
            handedOut.Add(job.GroupName);
            await shop.ReportAsync(job.PostId, ok: true);
        }
        Assert.Equal(["ลูกค้า 2"], handedOut); // the flagged customer is never handed out
        var skipped = (await shop.PostsAsync(created.Schedule.Id)).Single(p => p.Target == "ลูกค้า 1");
        Assert.Equal(PostStatus.Skipped, skipped.Status);
        Assert.Contains("ห้ามส่ง", skipped.FailureDetail ?? "");
    }

    [Fact]
    public async Task A_line_set_may_name_the_line_account_as_its_extension()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@postas_oa");
        var line = (await shop.DeviceAsync()).Line!;

        var created = await shop.Owner.PostAsJsonAsync($"{shop.Api}/link-sets", new { name = "ชุดที่ระบุเครื่อง", channel = "line", postAsAccountId = line.AccountId }, Json);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var set = (await created.Content.ReadFromJsonAsync<LinkSetDto>(Json))!;

        // The web echoes postAsAccountId on every rename: that must work too.
        var renamed = await shop.Owner.PutAsJsonAsync($"{shop.Api}/link-sets/{set.Id}", new { name = "ชื่อใหม่", postAsAccountId = line.AccountId, accountIds = Array.Empty<Guid>() }, Json);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        // A Facebook account is refused for a LINE set.
        var fbAccount = (await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"{shop.Api}/accounts", Json))!.Single(a => a.Platform == Platform.Fb);
        var refused = await shop.Owner.PutAsJsonAsync($"{shop.Api}/link-sets/{set.Id}", new { name = "ชื่อใหม่", postAsAccountId = fbAccount.Id, accountIds = Array.Empty<Guid>() }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task An_OA_is_refused_to_a_second_live_device_and_taken_over_after_its_browser_is_unbound()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@takeover_oa", oaName: "Takeover OA");
        var oldLine = (await shop.DeviceAsync()).Line!;

        // A second browser of the workspace reports the same OA while the first one is alive: refused, nothing flips.
        var (second, _) = await factory.PairDeviceAsync(shop.Owner, shop.Ws);
        var refused = await (await second.PostAsJsonAsync("/api/device/heartbeat",
            new { version = "2.5.0", line = new { ready = true, botId = "@takeover_oa", oaName = "Takeover OA" } }, Json)).ReadAsync<DeviceStatusDto>();
        Assert.Null(refused.LineAccountId);
        Assert.False(string.IsNullOrWhiteSpace(refused.LineNote));
        Assert.Single((await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"{shop.Api}/accounts", Json))!, a => a.Platform == Platform.Line);

        // The first browser is unbound: the OA (and its account, with its history) is taken over by the second browser.
        var devices = (await shop.Owner.GetFromJsonAsync<List<DeviceDto>>($"{shop.Api}/devices", Json))!;
        var first = devices.Single(d => d.Line is not null);
        (await shop.Owner.DeleteAsync($"{shop.Api}/devices/{first.Id}")).EnsureSuccessStatusCode();
        var taken = await (await second.PostAsJsonAsync("/api/device/heartbeat",
            new { version = "2.5.0", line = new { ready = true, botId = "@takeover_oa", oaName = "Takeover OA" } }, Json)).ReadAsync<DeviceStatusDto>();
        Assert.Equal(oldLine.AccountId, taken.LineAccountId);
        Assert.Null(taken.LineNote);
        Assert.Single((await shop.Owner.GetFromJsonAsync<List<AccountDto>>($"{shop.Api}/accounts", Json))!, a => a.Platform == Platform.Line);
    }

    [Fact]
    public async Task The_roster_filters_by_label_and_name()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@filter_oa");
        await SyncChatsAsync(shop, (1, "สมชาย", ["VIP", "ลูกค้าเก่า"]), (2, "สมหญิง", ["VIP"]), (3, "มานี", null));

        Assert.Equal(2, (await RosterAsync(shop, "?label=VIP")).Items.Count);
        Assert.Single((await RosterAsync(shop, "?label=" + Uri.EscapeDataString("ลูกค้าเก่า"))).Items);
        Assert.Equal(["สมหญิง"], (await RosterAsync(shop, "?q=" + Uri.EscapeDataString("หญิง"))).Items.Select(c => c.Name).ToList());
        var all = await RosterAsync(shop);
        Assert.Equal(3, all.Total);
        Assert.Contains("VIP", all.Labels);
    }

    [Fact]
    public async Task Import_needs_a_selection_and_takes_exactly_the_chats_picked()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@pick_oa");
        await SyncChatsAsync(shop, (1, "ลูกค้า 1", null), (2, "ลูกค้า 2", null), (3, "ลูกค้า 3", null));
        var roster = await RosterAsync(shop);
        var lineAccount = (await shop.DeviceAsync()).Line!.AccountId;
        var url = $"{shop.Api}/link-sets/{shop.Set.Id}/links/from-line-chats";

        // Nothing picked and "all matching" off: refused, never the whole roster by accident.
        var none = await shop.Owner.PostAsJsonAsync(url, new { accountId = lineAccount, allMatching = false }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        await shop.RefreshSetAsync();
        Assert.Empty(shop.Set.Links);

        var pick = roster.Items.Where(c => c.Name is "ลูกค้า 1" or "ลูกค้า 3").Select(c => c.Id).ToArray();
        var picked = await (await shop.Owner.PostAsJsonAsync(url, new { accountId = lineAccount, chatIds = pick, allMatching = false }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal(2, picked.Added);
        var again = await (await shop.Owner.PostAsJsonAsync(url, new { accountId = lineAccount, chatIds = pick, allMatching = false }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal((0, 2), (again.Added, again.Duplicates));
    }

    [Fact]
    public async Task A_complete_sync_keeps_do_not_send_chats_and_an_empty_list_removes_nothing()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@safe_oa");
        await SyncChatsAsync(shop, (1, "สมชาย", null), (2, "สมหญิง", null));
        var flagged = (await RosterAsync(shop)).Items.Single(c => c.Name == "สมชาย");
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/line/chats/{flagged.Id}", new { doNotSend = true }, Json)).EnsureSuccessStatusCode();

        // A failed scroll reports an empty "complete" list: nobody disappears.
        (await shop.Device.PutAsJsonAsync("/api/device/line/chats", new { botId = shop.LineBotId, oaName = shop.LineOaName, complete = true, chats = Array.Empty<object>() }, Json)).EnsureSuccessStatusCode();
        Assert.Equal(2, (await RosterAsync(shop)).Total);

        // A complete list that misses both customers removes the one that was not flagged, never the flagged one.
        (await shop.Device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = shop.LineBotId, oaName = shop.LineOaName, complete = true,
            chats = new[] { new { userId = UserId(9), name = "ใหม่", labels = Array.Empty<string>(), unread = false } },
        }, Json)).EnsureSuccessStatusCode();
        var names = (await RosterAsync(shop)).Items.Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Contains("สมชาย", names);
        Assert.DoesNotContain("สมหญิง", names);

        // A roster for another OA is refused (never merged into this one).
        var other = await shop.Device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = "@another_oa", oaName = "Other", complete = false,
            chats = new[] { new { userId = UserId(7), name = "คนอื่น", labels = Array.Empty<string>(), unread = false } },
        }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
    }

    [Fact]
    public async Task An_old_anti_ban_row_without_the_line_sections_reads_with_the_defaults()
    {
        using var shop = await factory.PairedLineShopAsync("pro", links: 0, posts: 1, botId: "@old_oa");
        var wsId = shop.Ws;
        await factory.WithDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"WORKSPACES\" SET anti_ban = (anti_ban - 'LineChat') #- '{{Limits,Line}}' WHERE id = {wsId}"));

        var engine = (await shop.Owner.GetFromJsonAsync<EngineSettingsDto>($"{shop.Api}/engine", Json))!;
        Assert.Equal((45, 120), (engine.AntiBan.LineChat!.MinGapSeconds, engine.AntiBan.LineChat.MaxGapSeconds));
        Assert.Equal(100, engine.AntiBan.Limits.Line);
    }
}
