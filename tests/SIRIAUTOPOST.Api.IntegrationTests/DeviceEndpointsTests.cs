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

    // ---------- a browser that pairs again, and sets pinned to a browser ----------

    private async Task<PairResultDto> PairAnotherAsync(Paired p, string name, object? previous = null)
    {
        var code = (await (await p.Owner.PostAsync($"/api/workspaces/{p.Ws}/devices/pairing", null)).Content
            .ReadFromJsonAsync<PairingCodeDto>(Json))!;
        var body = new Dictionary<string, object?> { ["code"] = code.Code, ["name"] = name, ["browser"] = "Chrome 154", ["version"] = "2.4.0" };
        if (previous is not null)
            foreach (var prop in previous.GetType().GetProperties()) body[prop.Name] = prop.GetValue(previous);
        var res = await factory.CreateClient().PostAsJsonAsync("/api/device/pair", body, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PairResultDto>(Json))!;
    }

    private async Task<List<AccountDto>> AccountsAsync(Paired p) =>
        (await p.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{p.Ws}/accounts", Json))!;

    [Fact]
    public async Task A_browser_that_pairs_again_while_still_paired_replaces_its_device_and_keeps_its_account()
    {
        var p = await PairAsync("pro");
        var set = await p.Owner.CreateLinkSetAsync(p.Ws, "siri", p.Pair.AccountId);

        var again = await PairAnotherAsync(p, "Chrome 154 7/10/2569", new { previousDeviceKey = p.Pair.DeviceKey });

        Assert.Equal(p.Pair.AccountId, again.AccountId); // the same Facebook account: its link sets and history stay with the browser
        Assert.NotEqual(p.Pair.DeviceId, again.DeviceId);
        Assert.Equal("Office PC", again.DeviceName); // the name it had in the web app, not the dated default of this pairing
        var device = Assert.Single((await p.Owner.GetFromJsonAsync<List<DeviceDto>>($"/api/workspaces/{p.Ws}/devices", Json))!); // no second, silent device
        Assert.Equal((again.DeviceId, p.Pair.AccountId), (device.Id, device.AccountId));
        var account = Assert.Single(await AccountsAsync(p), a => a.Id == p.Pair.AccountId);
        Assert.Equal(("Facebook · Office PC", true, AccountHealth.Ok), (account.Name, account.Connected, account.Health));
        Assert.Equal(p.Pair.AccountId, Assert.Single(await p.Owner.LinkSetsAsync(p.Ws), l => l.Id == set.Id).PostAsAccountId);

        // The old key no longer opens anything, the new one does.
        Assert.Equal(HttpStatusCode.Unauthorized, (await p.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);
        var fresh = factory.CreateClient();
        fresh.DefaultRequestHeaders.Add("X-Device-Key", again.DeviceKey);
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_browser_the_web_unbound_gets_its_account_back_when_it_pairs_again_and_no_other_live_account_is_taken()
    {
        var p = await PairAsync("agency");
        var other = await PairAnotherAsync(p, "Second PC"); // a second extension, connected
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}")).StatusCode);
        Assert.False(Assert.Single(await AccountsAsync(p), a => a.Id == p.Pair.AccountId).Connected);

        // It still remembers its account and name (no key: the device is gone).
        var back = await PairAnotherAsync(p, "Chrome 154 7/10/2569", new { previousAccountId = p.Pair.AccountId, previousName = "Office PC" });
        Assert.Equal((p.Pair.AccountId, "Office PC"), (back.AccountId, back.DeviceName));
        var account = Assert.Single(await AccountsAsync(p), a => a.Id == p.Pair.AccountId);
        Assert.Equal((true, AccountHealth.Ok), (account.Connected, account.Health));

        // An account another live browser holds is never taken over, whatever a pairing claims.
        var third = await PairAnotherAsync(p, "Third PC", new { previousAccountId = other.AccountId });
        Assert.NotEqual(other.AccountId, third.AccountId);
        Assert.True(Assert.Single(await AccountsAsync(p), a => a.Id == other.AccountId).Connected);

        // A key of another workspace (or an account of another workspace) means nothing here: it is a new browser.
        var stranger = await PairAsync();
        var fresh = await PairAnotherAsync(p, "Fourth PC", new { previousDeviceKey = stranger.Pair.DeviceKey, previousAccountId = stranger.Pair.AccountId });
        Assert.NotEqual(stranger.Pair.AccountId, fresh.AccountId);
        Assert.Equal(HttpStatusCode.OK, (await stranger.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_set_pinned_to_an_unbound_extension_waits_for_it_and_is_never_given_to_another_extension()
    {
        var p = await PairAsync("pro");
        var second = await PairAnotherAsync(p, "Second PC");
        var collection = await p.Owner.CreateCollectionAsync(p.Ws);
        await p.Owner.AddPostAsync(p.Ws, collection.Id);
        var pinned = await p.Owner.CreateLinkSetAsync(p.Ws, "siri", p.Pair.AccountId);
        await p.Owner.AddLinkAsync(p.Ws, pinned.Id, "https://www.facebook.com/groups/plants");
        // With two extensions connected a set cannot be made without choosing one (no "first one" guess).
        var unchosen = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/link-sets", new { name = "ทั่วไป" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unchosen.StatusCode);
        Assert.Contains("เลือกเครื่อง", await unchosen.Content.ReadAsStringAsync());
        object Body(Guid set) => new { collectionId = collection.Id, linkSetId = set, mode = "daily", times = new[] { "10:00" }, order = "rotate", utcOffsetMinutes = 420 };

        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", Body(pinned.Id), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync($"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}")).StatusCode);

        // Extension 1 is unbound: the siri set cannot be scheduled (it would have gone to extension 2)...
        var refused = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", Body(pinned.Id), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("ยังไม่ได้เชื่อมกับเครื่อง", await refused.Content.ReadAsStringAsync());
        // ...and nothing of it is queued for extension 2.
        var secondDevice = factory.CreateClient();
        secondDevice.DefaultRequestHeaders.Add("X-Device-Key", second.DeviceKey);
        using (factory.Clock.Advance(TimeSpan.FromDays(1)))
        {
            var claimed = await secondDevice.PostAsync("/api/device/jobs/claim", null);
            Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
        }
        // Only one extension is connected now: a set that names none is given to it when it is made, and can be scheduled.
        var automatic = await p.Owner.CreateLinkSetAsync(p.Ws, "ทั่วไป");
        Assert.Equal(second.AccountId, automatic.PostAsAccountId);
        await p.Owner.AddLinkAsync(p.Ws, automatic.Id, "https://www.facebook.com/groups/plants2");
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", Body(automatic.Id), Json)).StatusCode);
    }

    // ---------- one set, one extension; one group, one extension (2026-10-10) ----------

    private HttpClient DeviceClient(PairResultDto pair)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        return client;
    }

    private object StartNowBody(Guid collection, Guid set) =>
        new { collectionId = collection, linkSetId = set, mode = "daily", times = new[] { "18:00" }, order = "rotate", utcOffsetMinutes = 0, startNow = true };

    [Fact]
    public async Task A_group_belongs_to_one_extension_and_every_way_in_checks_it()
    {
        var p = await PairAsync("pro");
        var second = await PairAnotherAsync(p, "Second PC");
        var shop = await p.Owner.CreateLinkSetAsync(p.Ws, "ร้าน", p.Pair.AccountId);
        var office = await p.Owner.CreateLinkSetAsync(p.Ws, "ออฟฟิศ", second.AccountId);
        await p.Owner.AddLinkAsync(p.Ws, shop.Id, PlantsUrl);
        var links = $"/api/workspaces/{p.Ws}/link-sets/{office.Id}/links";

        // Adding the group to the other extension's set is refused, naming the set and the extension that hold it.
        var refused = await p.Owner.PostAsJsonAsync(links, new { url = "https://www.facebook.com/groups/Plants" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var why = await refused.Content.ReadAsStringAsync();
        Assert.Contains("ร้าน", why);
        Assert.Contains("Office PC", why);

        // An empty row is fine; typing the held address into it is refused too.
        var row = await p.Owner.AddLinkAsync(p.Ws, office.Id, null);
        var edit = await p.Owner.PutAsJsonAsync($"{links}/{row.Id}", new { name = "", url = PlantsUrl, code = "", dailyMax = 0, enabled = true }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);

        // A pasted list keeps the free addresses and reports the held one.
        var bulk = await (await p.Owner.PostAsJsonAsync($"{links}/bulk", new { text = $"{PlantsUrl}\nhttps://www.facebook.com/groups/free | F1" }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal((1, 1), (bulk.Added, bulk.Conflicts));
        Assert.Contains("ร้าน", bulk.ConflictNote);

        // The same for a CSV file.
        var csv = await (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/link-sets/import-csv", new
        {
            rows = new[]
            {
                new { set = "ออฟฟิศ", name = "", url = PlantsUrl, code = "" },
                new { set = "ออฟฟิศ", name = "", url = "https://www.facebook.com/groups/csv", code = "" },
            },
        }, Json)).ReadAsync<CsvImportResultDto>();
        Assert.Equal((1, 1), (csv.Links, csv.Conflicts));

        // Another set of the SAME extension may hold the group (one machine: nothing can mix).
        var shopToo = await p.Owner.CreateLinkSetAsync(p.Ws, "ร้าน 2", p.Pair.AccountId);
        await p.Owner.AddLinkAsync(p.Ws, shopToo.Id, PlantsUrl);

        // ...but a set cannot be handed to the other extension while its group stays in a set of this one.
        var move = await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/link-sets/{shop.Id}",
            new { name = "ร้าน", postAsAccountId = second.AccountId, accountIds = Array.Empty<Guid>() }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, move.StatusCode);
        Assert.Contains("ร้าน 2", await move.Content.ReadAsStringAsync());
        Assert.Equal(p.Pair.AccountId, (await p.Owner.LinkSetsAsync(p.Ws)).Single(s => s.Id == shop.Id).PostAsAccountId);

        // "No extension" is not an answer while two are connected.
        var none = await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/link-sets/{shopToo.Id}",
            new { name = "ร้าน 2", postAsAccountId = (Guid?)null, accountIds = Array.Empty<Guid>() }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Contains("เลือกเครื่อง", await none.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_post_is_only_handed_to_the_extension_of_its_set_even_when_the_set_moved_after_it_was_queued()
    {
        var p = await PairAsync("pro");
        var second = await PairAnotherAsync(p, "Second PC");
        var collection = await p.Owner.CreateCollectionAsync(p.Ws);
        await p.Owner.AddPostAsync(p.Ws, collection.Id);
        var set = await p.Owner.CreateLinkSetAsync(p.Ws, "siri", p.Pair.AccountId);
        await p.Owner.AddLinkAsync(p.Ws, set.Id, PlantsUrl);
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", StartNowBody(collection.Id, set.Id), Json)).StatusCode);

        // The set is handed to extension 2 behind the API's back (the way data from before the rule could be): its queued post is
        // still extension 1's.
        await factory.WithDbAsync(async db =>
        {
            var row = db.LinkSets.Single(s => s.Id == set.Id);
            row.Update(row.Name, second.AccountId, row.AccountIds);
            await db.SaveChangesAsync();
        });

        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
            var post = Assert.Single(await PostsOfAsync(p, p.Pair.AccountId), x => x.Status == PostStatus.Skipped);
            Assert.Contains("ไม่ใช่เครื่องนี้", post.FailureDetail);
            Assert.Contains("Second PC", post.FailureDetail);
        }
    }

    [Fact]
    public async Task A_group_that_two_extensions_already_held_before_the_rule_is_posted_by_the_one_that_held_it_first()
    {
        var p = await PairAsync("pro");
        var second = await PairAnotherAsync(p, "Second PC");
        var collection = await p.Owner.CreateCollectionAsync(p.Ws);
        await p.Owner.AddPostAsync(p.Ws, collection.Id);
        var shop = await p.Owner.CreateLinkSetAsync(p.Ws, "ร้าน", p.Pair.AccountId);
        await p.Owner.AddLinkAsync(p.Ws, shop.Id, PlantsUrl);
        var office = await p.Owner.CreateLinkSetAsync(p.Ws, "ออฟฟิศ", second.AccountId);
        // The same group in the other extension's set, as data from before the rule had it (the API would refuse it now).
        await factory.WithDbAsync(async db =>
        {
            db.SetLinks.Add(SIRIAUTOPOST.Domain.Entities.SetLink.Create(p.Ws, office.Id, null, PlantsUrl, null, 0, DateTimeOffset.UtcNow.AddDays(1)));
            await db.SaveChangesAsync();
        });
        foreach (var set in new[] { shop, office })
            Assert.Equal(HttpStatusCode.OK, (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", StartNowBody(collection.Id, set.Id), Json)).StatusCode);

        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            // Extension 2 holds it second: its post is skipped, never posted.
            Assert.Equal(HttpStatusCode.NoContent, (await DeviceClient(second).PostAsync("/api/device/jobs/claim", null)).StatusCode);
            var skipped = Assert.Single(await PostsOfAsync(p, second.AccountId), x => x.Status == PostStatus.Skipped);
            Assert.Contains("กลุ่มหนึ่งใช้ได้กับเครื่องเดียว", skipped.FailureDetail);

            // Extension 1 posts it.
            var claim = await p.Device.PostAsync("/api/device/jobs/claim", null);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            Assert.Equal(PlantsUrl, (await claim.Content.ReadFromJsonAsync<JobDto>(Json))!.GroupUrl);
        }
    }

    [Fact]
    public async Task Sets_that_name_nobody_are_given_to_the_extension_that_posts_them_when_a_second_one_pairs_and_at_start()
    {
        var p = await PairAsync("pro");
        // A set made while no browser was connected names nobody (the API would pin it now).
        var legacy = await factory.WithDbAsync(async db =>
        {
            var row = SIRIAUTOPOST.Domain.Entities.LinkSet.Create(p.Ws, "เก่า", null, DateTimeOffset.UtcNow, 50);
            db.LinkSets.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
        Assert.Null((await p.Owner.LinkSetsAsync(p.Ws)).Single(s => s.Id == legacy).PostAsAccountId);

        // The second extension pairs: the first one keeps what it was posting.
        var second = await PairAnotherAsync(p, "Second PC");
        Assert.Equal(p.Pair.AccountId, (await p.Owner.LinkSetsAsync(p.Ws)).Single(s => s.Id == legacy).PostAsAccountId);

        // The start-up pass gives a set that still names nobody to the first connected extension, once.
        var late = await factory.WithDbAsync(async db =>
        {
            var row = SIRIAUTOPOST.Domain.Entities.LinkSet.Create(p.Ws, "ตกค้าง", null, DateTimeOffset.UtcNow, 51);
            db.LinkSets.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
        await SIRIAUTOPOST.Infrastructure.Seeding.LinkSetOwnerBackfill.EnsureAsync(factory.Services);
        Assert.Equal(p.Pair.AccountId, (await p.Owner.LinkSetsAsync(p.Ws)).Single(s => s.Id == late).PostAsAccountId);
        Assert.NotEqual(second.AccountId, p.Pair.AccountId);
    }

    [Fact]
    public async Task A_schedule_for_a_set_that_names_nobody_is_refused_while_two_extensions_are_connected_and_says_to_choose_one()
    {
        var p = await PairAsync("pro");
        await PairAnotherAsync(p, "Second PC");
        var collection = await p.Owner.CreateCollectionAsync(p.Ws);
        await p.Owner.AddPostAsync(p.Ws, collection.Id);
        var orphan = await factory.WithDbAsync(async db =>
        {
            var row = SIRIAUTOPOST.Domain.Entities.LinkSet.Create(p.Ws, "ไม่มีเครื่อง", null, DateTimeOffset.UtcNow, 60);
            db.LinkSets.Add(row);
            db.SetLinks.Add(SIRIAUTOPOST.Domain.Entities.SetLink.Create(p.Ws, row.Id, null, PlantsUrl, null, 0, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return row.Id;
        });

        var refused = await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", StartNowBody(collection.Id, orphan), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("เลือกเครื่อง", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_set_that_still_names_other_accounts_is_cleared_at_start_and_their_queued_posts_are_removed_and_a_leftover_is_never_handed_out()
    {
        var p = await PairAsync("pro");
        var other = await PairAnotherAsync(p, "Second PC");
        var collection = await p.Owner.CreateCollectionAsync(p.Ws);
        var post = await p.Owner.AddPostAsync(p.Ws, collection.Id);
        var set = await p.Owner.CreateLinkSetAsync(p.Ws, "siri", p.Pair.AccountId);
        await p.Owner.AddLinkAsync(p.Ws, set.Id, PlantsUrl);
        var schedule = await (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/schedules", StartNowBody(collection.Id, set.Id), Json)).ReadAsync<ScheduleCreatedDto>();

        // Older data: the set names the other extension's account, and its schedule queued a post for it.
        var account = await factory.WithDbAsync(async db =>
        {
            var row = db.LinkSets.Single(s => s.Id == set.Id);
            row.GetType().GetProperty(nameof(row.AccountIds))!.SetValue(row, new List<Guid> { other.AccountId });
            var target = db.Accounts.Single(a => a.Id == other.AccountId);
            var legacy = SIRIAUTOPOST.Domain.Entities.Post.FromSchedule(p.Ws, target, "ฟีด", "ข้อความเก่า", [], DateTimeOffset.UtcNow.AddSeconds(5), DateTimeOffset.UtcNow,
                schedule.Schedule.Id, post.Id, null, SIRIAUTOPOST.Domain.Entities.Post.AccountTargetKey(target.Id), "legacy-slot", null, null);
            db.Posts.Add(legacy);
            await db.SaveChangesAsync();
            return target;
        });

        // The claim never hands it out, even before the start-up pass has run: it is skipped with the reason.
        using (factory.Clock.Advance(TimeSpan.FromMinutes(1)))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await DeviceClient(other).PostAsync("/api/device/jobs/claim", null)).StatusCode);
            var skipped = Assert.Single(await PostsOfAsync(p, account.Id), x => x.Status == PostStatus.Skipped);
            Assert.Contains("เครื่องเดียว", skipped.FailureDetail);
        }

        // A second leftover is removed by the start-up pass together with the list.
        await factory.WithDbAsync(async db =>
        {
            var target = db.Accounts.Single(a => a.Id == other.AccountId);
            db.Posts.Add(SIRIAUTOPOST.Domain.Entities.Post.FromSchedule(p.Ws, target, "ฟีด", "ข้อความเก่า 2", [], DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow,
                schedule.Schedule.Id, post.Id, null, SIRIAUTOPOST.Domain.Entities.Post.AccountTargetKey(target.Id), "legacy-slot-2", null, null));
            await db.SaveChangesAsync();
        });
        await SIRIAUTOPOST.Infrastructure.Seeding.LinkSetOwnerBackfill.EnsureAsync(factory.Services);

        Assert.Empty((await p.Owner.LinkSetsAsync(p.Ws)).Single(s => s.Id == set.Id).AccountIds);
        Assert.DoesNotContain(await PostsOfAsync(p, other.AccountId), x => x.Status == PostStatus.Queued);
        // What the set's own extension has queued is untouched.
        Assert.Contains(await PostsOfAsync(p, p.Pair.AccountId), x => x.LinkId is not null && x.Status == PostStatus.Queued);
    }

    private static async Task<List<PostDto>> PostsOfAsync(Paired p, Guid account) =>
        (await p.Owner.GetFromJsonAsync<List<PostDto>>(
            $"/api/workspaces/{p.Ws}/posts?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-2).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(21).ToString("O"))}", Json))!
        .Where(x => x.AccountId == account).ToList();

    [Fact]
    public async Task A_browser_that_is_logged_in_to_Facebook_again_takes_jobs_again_without_anybody_pressing_sign_in_again()
    {
        var p = await PairAsync();
        await p.Device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = PlantsUrl } } });
        var first = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(5));
        var second = await ScheduleAsync(p, "Plants", DateTimeOffset.UtcNow.AddMinutes(30));
        async Task<DeviceStatusDto> BeatAsync(bool? sessionOk = null) =>
            (await (await p.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "2.4.0", sessionOk }, Json)).Content.ReadFromJsonAsync<DeviceStatusDto>(Json))!;

        Assert.False((await BeatAsync()).SessionLost);
        using (factory.Clock.Advance(TimeSpan.FromMinutes(7)))
        {
            await p.Device.PostAsync("/api/device/jobs/claim", null);
            await p.Device.PostAsJsonAsync($"/api/device/jobs/{first.Id}/result", new { ok = false, needsLogin = true, error = "ยังไม่ได้ล็อกอิน Facebook ใน Chrome นี้" });
            Assert.True((await BeatAsync()).SessionLost); // the extension is told that the web waits for a login
        }
        using (factory.Clock.Advance(TimeSpan.FromMinutes(40)))
        {
            // Nothing is handed out while the login is missing, and a heartbeat that does not say it is back changes nothing.
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
            Assert.True((await BeatAsync(sessionOk: false)).SessionLost);
            Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);

            // The extension saw Facebook logged in: the account is healthy and the next post is handed out.
            Assert.False((await BeatAsync(sessionOk: true)).SessionLost);
            var job = (await (await p.Device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            Assert.Equal(second.Id, job.PostId);
        }
        Assert.Equal(AccountHealth.Ok, Assert.Single(await AccountsAsync(p), a => a.Id == p.Pair.AccountId).Health);
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
