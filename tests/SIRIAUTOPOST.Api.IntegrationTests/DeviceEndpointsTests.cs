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
        Assert.Equal((1, 1, true), (engine.Devices, engine.DevicesOnline, engine.ExtensionOnline));
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
}
