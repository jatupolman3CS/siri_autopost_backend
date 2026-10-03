using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DeviceEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private const string PlantsUrl = "https://www.facebook.com/groups/plants";

    private sealed record Paired(HttpClient Owner, Guid Ws, HttpClient Device, PairResultDto Pair);

    private async Task<Paired> PairAsync(string? plan = null)
    {
        var (owner, _, ws) = await factory.SignUpAsync(plan);
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content
            .ReadFromJsonAsync<PairingCodeDto>(Json))!;
        Assert.Matches("^[2-9A-Z]{4}-[2-9A-Z]{4}$", code.Code);

        var device = factory.CreateClient();
        // Typed by hand: lower case, no dash.
        var res = await device.PostAsJsonAsync("/api/device/pair",
            new { code = code.Code.Replace("-", " ").ToLowerInvariant(), name = "Office PC", browser = "Chrome 130", version = "2.1.0" }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pair = (await res.Content.ReadFromJsonAsync<PairResultDto>(Json))!;
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        return new Paired(owner, ws, device, pair);
    }

    private static async Task<PostDto> ScheduleAsync(Paired p, string group, DateTimeOffset at)
    {
        var res = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/schedule", new
        {
            content = "ต้นไม้มาใหม่",
            startAt = at,
            useDelay = false,
            repeat = "none",
            targets = new[] { new { accountId = p.Pair.AccountId, groups = new[] { group } } },
        }, Json);
        res.EnsureSuccessStatusCode();
        var posts = (await p.Owner.GetFromJsonAsync<List<PostDto>>(
            $"/api/workspaces/{p.Ws}/posts?from={Uri.EscapeDataString(at.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddMinutes(1).ToString("O"))}", Json))!;
        return posts.Single(x => x.AccountId == p.Pair.AccountId && x.Target == group && x.Status == PostStatus.Queued);
    }

    private static async Task<PostDto> PostAsync(Paired p, Guid id) =>
        (await p.Owner.GetFromJsonAsync<List<PostDto>>(
            $"/api/workspaces/{p.Ws}/posts?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(3).ToString("O"))}", Json))!
        .Single(x => x.Id == id);

    [Fact]
    public async Task Pairing_creates_a_connected_facebook_account_and_respects_the_plan()
    {
        var p = await PairAsync(); // free plan: one device
        var accounts = (await p.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{p.Ws}/accounts", Json))!;
        var mine = accounts.Single(a => a.Id == p.Pair.AccountId);
        Assert.True(mine.Connected);
        Assert.Equal(Platform.Fb, mine.Platform);
        Assert.All(accounts.Where(a => a.Id != mine.Id), a => Assert.False(a.Connected)); // demo accounts

        var more = await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/devices/pairing", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, more.StatusCode);

        var devices = (await p.Owner.GetFromJsonAsync<List<DeviceDto>>($"/api/workspaces/{p.Ws}/devices", Json))!;
        var d = Assert.Single(devices);
        Assert.Equal("Office PC", d.Name);
        Assert.True(d.Online);
        Assert.Equal(p.Pair.AccountId, d.AccountId);

        var engine = (await p.Owner.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{p.Ws}/engine", Json))!;
        Assert.Equal((1, 1, true, false), (engine.Devices, engine.DevicesOnline, engine.ExtensionOnline, engine.SimulatedOffline));

        // No heartbeat for over 100 seconds: the workspace reads as offline (not simulated).
        using (factory.Clock.Advance(TimeSpan.FromMinutes(3)))
        {
            engine = (await p.Owner.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{p.Ws}/engine", Json))!;
            Assert.Equal((0, false, false), (engine.DevicesOnline, engine.ExtensionOnline, engine.SimulatedOffline));
        }
    }

    [Fact]
    public async Task A_code_works_once_and_device_calls_need_the_key()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content
            .ReadFromJsonAsync<PairingCodeDto>(Json))!;
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "A" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await anon.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "B" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await anon.PostAsJsonAsync("/api/device/pair", new { code = "ZZZZ-ZZZZ", name = "C" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);
        anon.DefaultRequestHeaders.Add("X-Device-Key", "apd_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);
        // A user's token is not a device key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_extension_can_call_from_any_origin()
    {
        var p = await PairAsync();
        var req = new HttpRequestMessage(HttpMethod.Options, "/api/device/heartbeat");
        req.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "x-device-key,content-type");
        var res = await p.Device.SendAsync(req);
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal("*", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task A_due_post_goes_to_the_device_once_and_the_result_comes_back()
    {
        var p = await PairAsync();
        var hb = (await (await p.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "2.1.1" })).Content
            .ReadFromJsonAsync<DeviceStatusDto>(Json))!;
        Assert.Equal((p.Ws, p.Pair.AccountId, 0, true), (hb.WorkspaceId, hb.AccountId, hb.Groups, hb.Online));

        var sync = await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        Assert.Equal(1, await sync.Content.ReadFromJsonAsync<int>());
        var bad = await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "X", url = "javascript:alert(1)" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var post = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode); // not due yet

        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
        {
            var claim = await p.Device.PostAsync("/api/device/jobs/claim", null);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            var job = (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal((post.Id, "Plants", PlantsUrl, "ต้นไม้มาใหม่"), (job.PostId, job.GroupName, job.GroupUrl, job.Content));
            Assert.Equal(PostStatus.Posting, (await PostAsync(p, post.Id)).Status);
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode); // one at a time

            var done = await p.Device.PostAsJsonAsync($"/api/device/jobs/{post.Id}/result", new { ok = true });
            done.EnsureSuccessStatusCode();
            var sent = await PostAsync(p, post.Id);
            Assert.Equal(PostStatus.Success, sent.Status);
            Assert.NotNull(sent.PublishedAt);

            // The same result twice is refused.
            Assert.Equal(HttpStatusCode.NotFound, (await p.Device.PostAsJsonAsync($"/api/device/jobs/{post.Id}/result", new { ok = true })).StatusCode);
        }
    }

    [Fact]
    public async Task Waits_the_anti_ban_gap_and_reports_a_lost_login()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var first = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        var second = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(6));

        using (factory.Clock.Advance(TimeSpan.FromMinutes(7)))
        {
            await p.Device.PostAsync("/api/device/jobs/claim", null);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{first.Id}/result", new { ok = true });
            // The anti-ban minimum gap (3 minutes by default) is not over yet.
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
        }
        using (factory.Clock.Advance(TimeSpan.FromMinutes(11)))
        {
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(second.Id, job.PostId);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{second.Id}/result",
                new { ok = false, needsLogin = true, error = "ยังไม่ได้ล็อกอิน Facebook ใน Chrome นี้" });
        }
        var failed = await PostAsync(p, second.Id);
        Assert.Equal((PostStatus.Failed, FailureCode.Session), (failed.Status, failed.FailureCode));
        Assert.Equal("ยังไม่ได้ล็อกอิน Facebook ใน Chrome นี้", failed.FailureDetail);
        var account = (await p.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{p.Ws}/accounts", Json))!
            .Single(a => a.Id == p.Pair.AccountId);
        Assert.Equal(AccountHealth.Relogin, account.Health);

        // "Sign in again" on the error page lets the device post again.
        await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/accounts/{account.Id}/reconnect", null);
        await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{second.Id}/retry", null);
        using (factory.Clock.Advance(TimeSpan.FromMinutes(20)))
        {
            Assert.Equal(HttpStatusCode.OK, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
        }
    }

    [Fact]
    public async Task Late_posts_follow_the_offline_policy_and_unknown_groups_fail()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/engine/offline",
            new { policy = "skip", window = "2h", line = false, email = false, push = false }, Json);
        var late = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));

        // An hour late: under the skip policy it is not posted any more.
        using (factory.Clock.Advance(TimeSpan.FromMinutes(65)))
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
        Assert.Equal(PostStatus.Skipped, (await PostAsync(p, late.Id)).Status);

        // A group the extension no longer has.
        await p.Device.PutAsJsonAsync("/api/device/groups", new
        {
            groups = new[] { new { name = "Plants", url = PlantsUrl }, new { name = "Old", url = "https://www.facebook.com/groups/old" } },
        });
        var gone = await ScheduleAsync(p, "Old", DateTimeOffset.UtcNow.AddMinutes(5));
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
        var failed = await PostAsync(p, gone.Id);
        Assert.Equal(PostStatus.Failed, failed.Status);
        Assert.Contains("Old", failed.FailureDetail);
    }

    [Fact]
    public async Task Media_attached_to_a_job_downloads_with_the_device_key()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 9, 9]);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "file", "plant.jpg");
        var media = (await (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/media", form)).Content.ReadFromJsonAsync<MediaDto>(Json))!;

        var at = DateTimeOffset.UtcNow.AddMinutes(5);
        (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/schedule", new
        {
            content = "มีรูป", mediaIds = new[] { media.Id }, startAt = at, useDelay = false, repeat = "none",
            targets = new[] { new { accountId = p.Pair.AccountId, groups = new[] { "Plants" } } },
        }, Json)).EnsureSuccessStatusCode();

        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
        {
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            var m = Assert.Single(job.Media);
            Assert.Equal(("plant", "image/jpeg"), (m.Name, m.ContentType));
            Assert.Equal(6, (await p.Device.GetByteArrayAsync($"/api/device/media/{m.Id}")).Length);
        }
    }

    [Fact]
    public async Task Unbinding_a_device_locks_it_out_and_frees_the_slot()
    {
        var p = await PairAsync();
        var del = await p.Owner.DeleteAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await p.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);

        var account = (await p.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{p.Ws}/accounts", Json))!
            .Single(a => a.Id == p.Pair.AccountId);
        Assert.False(account.Connected);
        Assert.Equal(AccountHealth.Relogin, account.Health);
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/devices/pairing", null)).StatusCode);

        // Another user cannot see or unbind devices of this workspace.
        var (other, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/workspaces/{p.Ws}/devices")).StatusCode);
    }

    private static async Task<List<DeviceEventDto>> EventsAsync(Paired p, long after = 0) =>
        (await p.Owner.GetFromJsonAsync<DeviceEventsPageDto>($"/api/workspaces/{p.Ws}/events?after={after}&take=500", Json))!.Events.ToList();

    [Fact]
    public async Task Unbinding_fails_the_posts_that_could_never_be_sent_and_tells_the_stream_whoever_does_it()
    {
        var admin = await factory.AdminAsync();
        foreach (var byAdmin in new[] { false, true })
        {
            var p = await PairAsync();
            await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
            var queued = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddHours(2));
            var due = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
            PostDto? posting;
            using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
            {
                var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
                Assert.Equal(due.Id, job.PostId);
                posting = await PostAsync(p, due.Id);
                Assert.Equal(PostStatus.Posting, posting.Status);
            }

            var me = (await p.Owner.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;
            var res = byAdmin
                ? await admin.DeleteAsync($"/api/admin/customers/{me.Id}/devices/{p.Pair.DeviceId}")
                : await p.Owner.DeleteAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}");
            Assert.True(res.IsSuccessStatusCode);

            // What was handed out and what was still waiting both fail now: no new pairing claims them (it makes a new account).
            var unsent = await PostAsync(p, queued.Id);
            Assert.Equal((PostStatus.Failed, FailureCode.Session), (unsent.Status, unsent.FailureCode));
            Assert.Contains("จับคู่", unsent.FailureDetail);
            var lost = await PostAsync(p, due.Id);
            Assert.Equal((PostStatus.Failed, FailureCode.Network), (lost.Status, lost.FailureCode));

            var events = await EventsAsync(p);
            Assert.Contains(events, e => e.Type == "device.revoked" && e.Payload.GetProperty("name").GetString() == "Office PC");
            Assert.Equal(2, events.Count(e => e.Type == "post" && e.Payload.GetProperty("status").GetString() == "failed"));
        }
    }

    [Fact]
    public async Task What_the_device_changes_alone_reaches_the_web_app_as_events()
    {
        var p = await PairAsync();
        var head = (await p.Owner.GetFromJsonAsync<DeviceEventsPageDto>($"/api/workspaces/{p.Ws}/events?after=0&take=1", Json))!.Head;

        // New groups are an event, the same list again is not.
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        // Renaming and pausing from the web are events too.
        (await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}", new { name = "Shop PC", jobsPaused = true }, Json)).EnsureSuccessStatusCode();
        // A post settled while the device claims (a group it no longer has) is an event as well.
        var gone = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}", new { jobsPaused = false }, Json);
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Other", url = "https://www.facebook.com/groups/other" } } });
        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
            await p.Device.PostAsync("/api/device/jobs/claim", null);

        var events = await EventsAsync(p, head);
        var groups = events.Where(e => e.Type == "device.groups").ToList();
        Assert.Equal([1, 1], groups.Select(e => e.Payload.GetProperty("count").GetInt32()));
        var updated = events.Where(e => e.Type == "device.updated").Select(e => (e.Payload.GetProperty("name").GetString(), e.Payload.GetProperty("jobsPaused").GetBoolean())).ToList();
        Assert.Equal([("Shop PC", true), ("Shop PC", false)], updated);
        var failed = Assert.Single(events, e => e.Type == "post" && e.Payload.GetProperty("postId").GetGuid() == gone.Id);
        Assert.Equal("failed", failed.Payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_connected_account_with_no_synced_groups_cannot_be_scheduled()
    {
        var p = await PairAsync();
        var res = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/schedule", new
        {
            content = "ยังไม่มีกลุ่ม", startAt = DateTimeOffset.UtcNow.AddHours(1), useDelay = false, repeat = "none",
            targets = new[] { new { accountId = p.Pair.AccountId } },
        }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("ยังไม่มีกลุ่ม", await res.Content.ReadAsStringAsync());

        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        Assert.NotNull(await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task The_anti_ban_gap_is_pacing_not_lateness_so_the_skip_policy_keeps_the_second_post()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/engine/offline",
            new { policy = "skip", window = "2h", line = false, email = false, push = false }, Json);
        var engine = (await p.Owner.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{p.Ws}/engine", Json))!;
        await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/engine/anti-ban", engine.AntiBan with { Min = 30, Max = 40 }, Json);
        var at = DateTimeOffset.UtcNow.AddMinutes(5);
        var first = await ScheduleAsync(p, "Plants", at);
        var second = await ScheduleAsync(p, "Plants", at.AddMinutes(5)); // due soon after the first

        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
        {
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(first.Id, job.PostId);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{job.PostId}/result", new { ok = true });
        }
        // 38 minutes after the first one went out, far past the skip policy's 10 minutes after its due time, but only 8 after
        // the anti-ban gap (30 minutes) let it go: held back by the gap, not late.
        using (factory.Clock.Advance(TimeSpan.FromMinutes(38)))
        {
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(second.Id, job.PostId);
        }
    }

    [Fact]
    public async Task A_suspended_customers_device_is_told_to_pause_and_gets_no_start_buttons()
    {
        var admin = await factory.AdminAsync();
        var p = await PairAsync();
        var me = (await p.Owner.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;
        var cmd = $"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}/commands";
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync(cmd, new { cmd = "start" }, Json)).StatusCode);
        Assert.False((await Heartbeat(p)).JobsPaused);

        // The owner is paused by the platform admin: no jobs, and none of the buttons that make the browser post.
        await admin.PostAsJsonAsync($"/api/admin/customers/{me.Id}/pause", new { paused = true }, Json);
        Assert.True((await Heartbeat(p)).JobsPaused);
        foreach (var blocked in new[] { "start", "runNow", "testPost" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await p.Owner.PostAsJsonAsync(cmd, new { cmd = blocked }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync(cmd, new { cmd = "stop" }, Json)).StatusCode); // stopping is always allowed

        await admin.PostAsJsonAsync($"/api/admin/customers/{me.Id}/pause", new { paused = false }, Json);
        Assert.False((await Heartbeat(p)).JobsPaused);
    }

    private static async Task<DeviceStatusDto> Heartbeat(Paired p) =>
        (await (await p.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "2.2.0" }, Json)).Content.ReadFromJsonAsync<DeviceStatusDto>(Json))!;

    [Fact]
    public async Task A_state_too_big_for_an_event_still_syncs_and_the_web_fetches_it()
    {
        var p = await PairAsync();
        var big = new { groups = Enumerable.Range(0, 20_000).Select(i => new { name = "กลุ่มทดสอบที่ " + i, url = "https://www.facebook.com/groups/" + i }).ToArray() };
        var res = await p.Device.PostAsJsonAsync("/api/device/sync", new { version = "2.2.0", state = big, takeCommands = false }, Json);
        res.EnsureSuccessStatusCode();
        var state = Assert.Single(await EventsAsync(p), e => e.Type == "device.state");
        Assert.True(state.Payload.GetProperty("truncated").GetBoolean());
        var live = (await p.Owner.GetFromJsonAsync<DeviceLiveDto>($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}/live", Json))!;
        Assert.Equal(20_000, live.State!.Value.GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public async Task Scheduling_with_a_library_file_counts_one_use_and_members_see_the_owners_limits()
    {
        var (owner, _, ws) = await factory.SignUpAsync("agency");
        var (member, memberAuth, _) = await factory.SignUpAsync(); // on Free themselves
        (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/members", new { email = memberAuth.User.Email, role = "admin" }, Json)).EnsureSuccessStatusCode();

        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1]);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "file", "a.jpg");
        var media = (await (await owner.PostAsync($"/api/workspaces/{ws}/media", form)).Content.ReadFromJsonAsync<MediaDto>(Json))!;
        var accounts = (await owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        var ig = accounts.First(a => a.Platform == Platform.Ig).Id;
        (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "สามวัน", mediaIds = new[] { media.Id }, startAt = DateTimeOffset.UtcNow.AddDays(1), useDelay = false, repeat = "daily",
            targets = new[] { new { accountId = ig } },
        }, Json)).EnsureSuccessStatusCode(); // 14 posts, one use
        var used = (await owner.GetFromJsonAsync<List<MediaDto>>($"/api/workspaces/{ws}/media", Json))!.Single(m => m.Id == media.Id);
        Assert.Equal(1, used.UsedCount);

        // The member's own plan is Free, the workspace's owner is on Agency: the workspace says what applies there.
        var mine = (await member.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single(w => w.Id == ws);
        Assert.Equal((WorkspaceRole.Admin, true), (mine.Role, mine.AdvancedAntiBan));
        Assert.Equal(new LimitsDto(null, null, null, 10), mine.Limits);
    }
}
