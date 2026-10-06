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
    public async Task Retrying_many_failed_posts_queues_the_failed_ones_and_says_what_it_left_alone()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var failing = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        var waiting = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(60));
        using (factory.Clock.Advance(TimeSpan.FromMinutes(7)))
        {
            await p.Device.PostAsync("/api/device/jobs/claim", null);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{failing.Id}/result", new { ok = false, error = "Facebook ไม่ตอบสนอง" });
        }
        Assert.Equal(PostStatus.Failed, (await PostAsync(p, failing.Id)).Status);

        var unknown = Guid.NewGuid();
        var res = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/retry", new { postIds = new[] { failing.Id, waiting.Id, unknown, failing.Id } }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var result = (await res.Content.ReadFromJsonAsync<RetryResult>(Json))!;
        // One failed post retried; the queued one and the id nobody has are "not failed" (the repeated id counts once).
        Assert.Equal((1, 0, 2), (result.Retried, result.Unbound, result.NotFailed));

        var again = await PostAsync(p, failing.Id);
        Assert.Equal(PostStatus.Queued, again.Status);
        Assert.Null(again.FailureCode);
        Assert.True(again.ScheduledAt > DateTimeOffset.UtcNow.AddMinutes(10));
        var errors = (await p.Owner.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{p.Ws}/errors", Json))!;
        Assert.DoesNotContain(errors, e => e.Id == failing.Id);

        // Retrying it a second time finds nothing failed.
        var twice = (await (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/retry", new { postIds = new[] { failing.Id } }, Json))
            .Content.ReadFromJsonAsync<RetryResult>(Json))!;
        Assert.Equal((0, 0, 1), (twice.Retried, twice.Unbound, twice.NotFailed));
    }

    [Fact]
    public async Task Retrying_many_leaves_the_posts_of_an_unbound_browser_failed_and_checks_the_ids()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var one = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        var two = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(6));
        // Unbinding the browser fails what it still had queued; nothing would ever take those posts again.
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}")).StatusCode);
        Assert.Equal(PostStatus.Failed, (await PostAsync(p, one.Id)).Status);

        var res = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/retry", new { postIds = new[] { one.Id, two.Id } }, Json);
        var result = (await res.Content.ReadFromJsonAsync<RetryResult>(Json))!;
        Assert.Equal((0, 2, 0), (result.Retried, result.Unbound, result.NotFailed));
        Assert.Equal(PostStatus.Failed, (await PostAsync(p, one.Id)).Status);
        Assert.Equal(2, (await p.Owner.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{p.Ws}/errors", Json))!.Count(e => e.Id == one.Id || e.Id == two.Id));

        // Nothing chosen, or more than 500 at a time, is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/retry", new { postIds = Array.Empty<Guid>() }, Json)).StatusCode);
        var tooMany = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/retry", new { postIds = tooMany }, Json)).StatusCode);
        // Someone outside the workspace has no access to it at all.
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/retry", new { postIds = new[] { one.Id } }, Json)).StatusCode);
    }

    private sealed record RetryResult(int Retried, int Unbound, int NotFailed);

    [Fact]
    public async Task Post_now_takes_a_post_out_of_its_slot_and_it_goes_before_the_older_due_posts()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var older = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        var newer = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(6));
        var later = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(300));

        using (factory.Clock.Advance(TimeSpan.FromMinutes(8)))
        {
            // Two posts are due; the third is hours away. "Post now" on it is the browser's next job.
            var res = await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{later.Id}/run-now", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var rushed = (await res.Content.ReadFromJsonAsync<PostDto>(Json))!;
            Assert.True(rushed.Rushed);
            Assert.Equal(PostStatus.Queued, rushed.Status);
            Assert.True(rushed.ScheduledAt < DateTimeOffset.UtcNow.AddMinutes(60), "it left its slot, 5 hours ahead");

            // The browser is told to take it now, not at its next 30-second round.
            var taken = (await (await p.Device.PostAsJsonAsync("/api/device/sync", new { version = "2.2.1", takeCommands = true }, Json)).Content
                .ReadFromJsonAsync<DeviceSyncDto>(Json))!;
            Assert.Contains(taken.Commands, c => c.Cmd == "takeJobs");

            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(later.Id, job.PostId);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{later.Id}/result", new { ok = true }, Json);
        }

        // Nothing is left to run at the old time, and the two others still wait their turn.
        var sent = await PostAsync(p, later.Id);
        Assert.Equal(PostStatus.Success, sent.Status);
        Assert.False(sent.Rushed);
        Assert.True(sent.ScheduledAt < DateTimeOffset.UtcNow.AddMinutes(60));
        Assert.Equal(PostStatus.Queued, (await PostAsync(p, older.Id)).Status);
        Assert.Equal(PostStatus.Queued, (await PostAsync(p, newer.Id)).Status);
    }

    [Fact]
    public async Task Post_now_reruns_a_failed_post_at_once_and_refuses_what_cannot_be_sent_again()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var failing = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        var sent = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(30));
        using (factory.Clock.Advance(TimeSpan.FromMinutes(7)))
        {
            await p.Device.PostAsync("/api/device/jobs/claim", null);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{failing.Id}/result", new { ok = false, error = "Facebook ไม่ตอบสนอง" });
        }
        Assert.Equal(PostStatus.Failed, (await PostAsync(p, failing.Id)).Status);

        // Not 15 minutes later, like the retry: this very moment.
        var res = await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{failing.Id}/run-now", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var again = await PostAsync(p, failing.Id);
        Assert.Equal(PostStatus.Queued, again.Status);
        Assert.True(again.Rushed);
        Assert.Null(again.FailureCode);
        Assert.True(again.ScheduledAt < DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.DoesNotContain((await p.Owner.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{p.Ws}/errors", Json))!, e => e.Id == failing.Id);

        // A post that is being posted, or went out, is not sent a second time.
        using (factory.Clock.Advance(TimeSpan.FromMinutes(40)))
        {
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(failing.Id, job.PostId);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{failing.Id}/run-now", null)).StatusCode);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{failing.Id}/result", new { ok = true }, Json);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{failing.Id}/run-now", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{Guid.NewGuid()}/run-now", null)).StatusCode);

        // A stranger has no access; a browser that was unbound cannot take the post, so it is refused rather than left queued for ever.
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/workspaces/{p.Ws}/posts/{sent.Id}/run-now", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/posts/{sent.Id}/run-now", null)).StatusCode);
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
    public async Task A_video_whose_index_is_at_the_end_reaches_the_device_with_the_index_first()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        byte[] Box(string type, byte[] body)
        {
            var b = new byte[8 + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            body.CopyTo(b, 8);
            return b;
        }
        // ftyp, mdat, moov: what an export that writes the index last leaves (the Facebook composer never finished with such a file)
        byte[] video = [.. Box("ftyp", System.Text.Encoding.ASCII.GetBytes("isom\0\0\u0002\0isomiso2avc1mp41")), .. Box("mdat", [1, 2, 3, 4, 5, 6, 7, 8]), .. Box("moov", Box("mvhd", new byte[100]))];
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(video);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        form.Add(content, "file", "promo.mp4");
        var media = (await (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/media", form)).Content.ReadFromJsonAsync<MediaDto>(Json))!;

        (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/posts/schedule", new
        {
            content = "มีวิดีโอ", mediaIds = new[] { media.Id }, startAt = DateTimeOffset.UtcNow.AddMinutes(5), useDelay = false, repeat = "none",
            targets = new[] { new { accountId = p.Pair.AccountId, groups = new[] { "Plants" } } },
        }, Json)).EnsureSuccessStatusCode();

        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
        {
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            var served = await p.Device.GetByteArrayAsync($"/api/device/media/{Assert.Single(job.Media).Id}");
            var types = new List<string>();
            for (var pos = 0; pos < served.Length;)
            {
                types.Add(System.Text.Encoding.ASCII.GetString(served, pos + 4, 4));
                pos += (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(served.AsSpan(pos, 4));
            }
            Assert.Equal(new[] { "ftyp", "moov", "mdat" }, types);
            Assert.Equal(video.Length, served.Length); // lossless: the same boxes, only reordered
            // The library keeps the file as it was uploaded.
            Assert.Equal(video, await p.Owner.GetByteArrayAsync($"/api/workspaces/{p.Ws}/media/{media.Id}/content"));
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
        var ig = (await factory.SeedAccountAsync(ws)).Id;
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
        Assert.Equal(new LimitsDto(null, null, null, 10, null, null, null), mine.Limits);
    }
}
