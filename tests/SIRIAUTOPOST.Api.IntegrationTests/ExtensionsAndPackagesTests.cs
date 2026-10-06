using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;
using static SIRIAUTOPOST.Api.IntegrationTests.WorkflowTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Several extensions in one workspace, the test posts that choose one, the packages' size limits, the AI writer and bumping.
[Collection(ApiCollection.Name)]
public sealed class ExtensionsAndPackagesTests(ApiFactory factory)
{
    private static async Task<string> TitleAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<ProblemTitle>(Json))?.Title ?? "";

    private sealed record ProblemTitle(string? Title);

    // ---------- extension names ----------

    [Fact]
    public async Task Two_browsers_that_pair_with_the_same_name_get_different_names_and_a_rename_cannot_repeat_one()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro"); // three devices
        var (_, first) = await factory.PairDeviceAsync(owner, ws);
        var (_, second) = await factory.PairDeviceAsync(owner, ws);
        var (_, third) = await factory.PairDeviceAsync(owner, ws);

        Assert.Equal(["Shop PC", "Shop PC (2)", "Shop PC (3)"], [first.DeviceName, second.DeviceName, third.DeviceName]);
        var devices = (await owner.GetFromJsonAsync<List<DeviceDto>>($"/api/workspaces/{ws}/devices", Json))!;
        Assert.Equal(3, devices.Select(d => d.Name.ToLowerInvariant()).Distinct().Count());

        // The same name again (whatever its case) is refused with the reason; the device keeps its own name.
        var clash = await owner.PutAsJsonAsync($"/api/workspaces/{ws}/devices/{second.DeviceId}", new { name = "shop pc" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        Assert.Contains("ชื่อ", await TitleAsync(clash));
        var kept = (await owner.GetFromJsonAsync<List<DeviceDto>>($"/api/workspaces/{ws}/devices", Json))!.Single(d => d.Id == second.DeviceId);
        Assert.Equal("Shop PC (2)", kept.Name);

        // A device may keep its own name when it is "renamed" to it, and a free name is fine (its account follows).
        (await owner.PutAsJsonAsync($"/api/workspaces/{ws}/devices/{second.DeviceId}", new { name = "Shop PC (2)" }, Json)).EnsureSuccessStatusCode();
        var renamed = await owner.PutAsJsonAsync($"/api/workspaces/{ws}/devices/{second.DeviceId}", new { name = "คอมที่ร้าน" }, Json);
        Assert.Equal("คอมที่ร้าน", (await renamed.ReadAsync<DeviceDto>()).Name);
        var accounts = (await owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        Assert.Contains(accounts, a => a.Id == second.AccountId && a.Name == "Facebook · คอมที่ร้าน");

        // Another workspace may use the same names.
        var (other, _, otherWs) = await factory.SignUpAsync();
        var (_, elsewhere) = await factory.PairDeviceAsync(other, otherWs);
        Assert.Equal("Shop PC", elsewhere.DeviceName);
    }

    // ---------- test posts: which extension, and typed by hand ----------

    [Fact]
    public async Task A_test_post_goes_to_the_extension_that_was_chosen()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 1);
        var (device2, second) = await factory.PairDeviceAsync(shop.Owner, shop.Ws);

        var first = await (await shop.Owner.PostAsJsonAsync($"{shop.Api}/test-post", new
        {
            linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id,
        }, Json)).ReadAsync<PostDto>();
        Assert.Equal(shop.Pair.AccountId, first.AccountId); // the set names the first browser

        var chosen = await (await shop.Owner.PostAsJsonAsync($"{shop.Api}/test-post", new
        {
            linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id, deviceId = second.DeviceId,
        }, Json)).ReadAsync<PostDto>();
        Assert.Equal(second.AccountId, chosen.AccountId);

        var job = await (await device2.PostAsync("/api/device/jobs/claim", null)).ReadAsync<JobDto>();
        Assert.Equal(chosen.Id, job.PostId);

        var unknown = await shop.Owner.PostAsJsonAsync($"{shop.Api}/test-post", new
        {
            linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id, deviceId = Guid.NewGuid(),
        }, Json);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_manual_test_post_sends_a_typed_address_text_and_images_to_the_chosen_extension()
    {
        using var shop = await factory.ShopAsync(links: 0, posts: 0);
        var (device2, second) = await factory.PairDeviceAsync(shop.Owner, shop.Ws);
        var image = await shop.Owner.UploadImageAsync(shop.Ws);
        string Url() => $"{shop.Api}/test-post/manual";

        // With two extensions connected, the person must say which one.
        var ambiguous = await shop.Owner.PostAsJsonAsync(Url(), new { url = "facebook.com/groups/plants", text = "ทดสอบ" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ambiguous.StatusCode);
        Assert.Contains("เลือกส่วนขยาย", await TitleAsync(ambiguous));

        var post = await (await shop.Owner.PostAsJsonAsync(Url(), new
        {
            url = "https://m.facebook.com/groups/plants/?ref=share", text = "{สวัสดี|หวัดดี} ทดสอบ {{code}}", mediaIds = new[] { image.Id }, deviceId = second.DeviceId,
        }, Json)).ReadAsync<PostDto>();
        Assert.Equal((second.AccountId, "https://www.facebook.com/groups/plants", true, PostStatus.Queued), (post.AccountId, post.TargetUrl, post.IsTest, post.Status));
        Assert.DoesNotContain("{", post.Content); // spintax picked, a code tag with no group code leaves nothing behind
        Assert.Equal([image.Id], post.MediaIds);

        var job = await (await device2.PostAsync("/api/device/jobs/claim", null)).ReadAsync<JobDto>();
        Assert.Equal(("https://www.facebook.com/groups/plants", 1, "post", "group"), (job.GroupUrl, job.Media.Count, job.Kind, job.TargetKind));

        // A page address works too and tells the extension to act as the page; a stranger's workspace image or a bad address is refused.
        var page = await (await shop.Owner.PostAsJsonAsync(Url(), new { url = "facebook.com/baandee.shop", text = "เพจ", deviceId = shop.Pair.DeviceId }, Json)).ReadAsync<PostDto>();
        Assert.Equal("https://www.facebook.com/baandee.shop", page.TargetUrl);
        var job2 = await (await shop.Device.PostAsync("/api/device/jobs/claim", null)).ReadAsync<JobDto>();
        Assert.Equal("page", job2.TargetKind);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await shop.Owner.PostAsJsonAsync(Url(), new { url = "https://example.com/x", text = "x", deviceId = second.DeviceId }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await shop.Owner.PostAsJsonAsync(Url(), new { url = "facebook.com/groups/plants", text = "x", mediaIds = new[] { Guid.NewGuid() }, deviceId = second.DeviceId }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await shop.Owner.PostAsJsonAsync(Url(), new { url = "facebook.com/groups/plants", text = "", deviceId = second.DeviceId }, Json)).StatusCode);

        // Editors send tests, viewers do not.
        var (viewer, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync(Url(), new { url = "facebook.com/groups/plants", text = "x" }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_single_extension_needs_no_choice_for_a_manual_test()
    {
        using var shop = await factory.ShopAsync(links: 0, posts: 0);
        var post = await (await shop.Owner.PostAsJsonAsync($"{shop.Api}/test-post/manual", new { url = "facebook.com/groups/plants", text = "ทดสอบ" }, Json)).ReadAsync<PostDto>();
        Assert.Equal(shop.Pair.AccountId, post.AccountId);
    }

    // ---------- pages as targets ----------

    [Fact]
    public async Task A_link_set_takes_facebook_pages_as_well_as_groups()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var set = await client.CreateLinkSetAsync(ws);

        var result = await (await client.PostAsJsonAsync($"/api/workspaces/{ws}/link-sets/{set.Id}/links/bulk", new
        {
            text = "facebook.com/groups/plants | G1\nhttps://www.facebook.com/baandee.shop | P1\nfacebook.com/profile.php?id=100012345678901\nfacebook.com/watch\nexample.com/x",
        }, Json)).ReadAsync<BulkLinksResultDto>();

        Assert.Equal((3, 2), (result.Added, result.Invalid));
        var links = result.Set.Links;
        Assert.Equal(["group", "page", "page"], links.Select(l => l.Kind));
        Assert.All(links, l => Assert.True(l.Valid));
    }

    // ---------- the package's size limits ----------

    [Fact]
    public async Task Free_holds_ten_groups_twenty_images_and_twenty_library_posts_and_billing_says_how_much_is_used()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);
        string Lines(int from, int count) => string.Join("\n", Enumerable.Range(from, count).Select(i => $"facebook.com/groups/g{i}"));
        string Bulk() => $"/api/workspaces/{ws}/link-sets/{set.Id}/links/bulk";

        // Groups: all or nothing, with the plan's number and the usage in the reason.
        var over = await client.PostAsJsonAsync(Bulk(), new { text = Lines(0, 11) }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Contains("10", await TitleAsync(over));
        Assert.Empty((await client.LinkSetsAsync(ws)).Single().Links);
        (await client.PostAsJsonAsync(Bulk(), new { text = Lines(0, 10) }, Json)).EnsureSuccessStatusCode();
        var full = await client.PostAsJsonAsync($"/api/workspaces/{ws}/link-sets/{set.Id}/links", new { url = "facebook.com/groups/one-more" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, full.StatusCode);

        // Images.
        for (var i = 0; i < 20; i++) await client.UploadImageAsync(ws, $"p{i}.jpg");
        var tooMany = await client.PostAsync($"/api/workspaces/{ws}/media", Image("extra.jpg"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Contains("20", await TitleAsync(tooMany));

        // Posts of the library.
        var collection = await client.CreateCollectionAsync(ws);
        for (var i = 0; i < 20; i++) await client.AddPostAsync(ws, collection.Id, $"โพสต์ {i}");
        var tooManyPosts = await client.PostAsJsonAsync($"/api/workspaces/{ws}/master-posts", new { text = "โพสต์ที่ 21", active = true }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooManyPosts.StatusCode);

        var billing = (await client.GetFromJsonAsync<BillingDto>("/api/billing", Json))!;
        Assert.Equal(new LimitsDto(1, 10, 1, 1, 10, 20, 20), billing.Limits);
        Assert.Equal((10, 20, 20), (billing.Usage.Groups, billing.Usage.Images, billing.Usage.LibraryPosts));

        // The platform admin can lift one of them for one customer (0 = unlimited).
        var admin = await factory.AdminAsync();
        var me = (await client.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;
        (await admin.PutAsJsonAsync($"/api/admin/customers/{me.Id}/limits", new { groups = 0, images = 30 }, Json)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Bulk(), new { text = Lines(100, 5) }, Json)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/workspaces/{ws}/media", Image("ten-more.jpg"))).EnsureSuccessStatusCode();
    }

    private static MultipartFormDataContent Image(string name)
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "file", name);
        return form;
    }

    [Fact]
    public async Task The_plans_list_their_limits_and_features()
    {
        var plans = (await factory.CreateClient().GetFromJsonAsync<List<PlanDto>>("/api/plans", Json))!;
        var byKey = plans.ToDictionary(p => p.Key);

        Assert.Equal((10, 20, 20), (byKey[PlanKey.Free].Groups, byKey[PlanKey.Free].Images, byKey[PlanKey.Free].LibraryPosts));
        Assert.Empty(byKey[PlanKey.Free].Features);
        Assert.Contains("ai", byKey[PlanKey.Pro].Features);
        Assert.DoesNotContain("bump", byKey[PlanKey.Pro].Features);
        Assert.Contains("bump", byKey[PlanKey.Agency].Features);
        Assert.Null(byKey[PlanKey.Agency].Groups); // unlimited

        var (client, _, _) = await factory.SignUpAsync("agency");
        var ws = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single();
        Assert.Equal((true, true), (ws.Ai, ws.Bump));
        var (pro, _, _) = await factory.SignUpAsync("pro");
        var proWs = (await pro.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single();
        Assert.Equal((true, false), (proWs.Ai, proWs.Bump));
    }

    // ---------- the AI writer ----------

    [Fact]
    public async Task The_ai_writer_needs_a_key_on_the_server_the_plan_and_stays_within_the_days_allowance()
    {
        var (free, _, freeWs) = await factory.SignUpAsync();
        var (pro, _, proWs) = await factory.SignUpAsync("pro");
        var body = new { topic = "ขายต้นไม้", points = new[] { "ส่งฟรี" }, tone = "sales", count = 2 };

        // Free: the status says it is not included, and writing is refused.
        var freeStatus = (await free.GetFromJsonAsync<AiStatusDto>($"/api/workspaces/{freeWs}/ai/status", Json))!;
        Assert.Equal((true, false), (freeStatus.Enabled, freeStatus.Allowed));
        Assert.Equal(HttpStatusCode.Forbidden, (await free.PostAsJsonAsync($"/api/workspaces/{freeWs}/ai/posts", body, Json)).StatusCode);

        // Pro with a key: drafts come back (not saved anywhere) and the allowance goes down.
        var status = (await pro.GetFromJsonAsync<AiStatusDto>($"/api/workspaces/{proWs}/ai/status", Json))!;
        Assert.Equal((true, true, "fake-model", 50), (status.Enabled, status.Allowed, status.Model, status.DraftsLeftToday));
        var drafts = await (await pro.PostAsJsonAsync($"/api/workspaces/{proWs}/ai/posts", body, Json)).ReadAsync<AiDraftsDto>();
        Assert.Equal(2, drafts.Variants.Count);
        Assert.Contains("ขายต้นไม้", drafts.Variants[0]);
        Assert.Equal(("sales", 2), (factory.Ai.Requests[^1].Tone, factory.Ai.Requests[^1].Count));
        Assert.Equal(48, (await pro.GetFromJsonAsync<AiStatusDto>($"/api/workspaces/{proWs}/ai/status", Json))!.DraftsLeftToday);

        // Bad input is a 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await pro.PostAsJsonAsync($"/api/workspaces/{proWs}/ai/posts", new { topic = "", count = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await pro.PostAsJsonAsync($"/api/workspaces/{proWs}/ai/posts", new { topic = "x", count = 9 }, Json)).StatusCode);

        // The day's allowance (50 drafts) runs out.
        for (var i = 0; i < 9; i++)
            (await pro.PostAsJsonAsync($"/api/workspaces/{proWs}/ai/posts", new { topic = "x", count = 5 }, Json)).EnsureSuccessStatusCode();
        var spent = await pro.PostAsJsonAsync($"/api/workspaces/{proWs}/ai/posts", new { topic = "x", count = 5 }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, spent.StatusCode);

        // No key on the server: the status says so and writing is refused with the reason.
        var (pro2, _, ws2) = await factory.SignUpAsync("pro");
        factory.Ai.IsOn = false;
        try
        {
            var off = (await pro2.GetFromJsonAsync<AiStatusDto>($"/api/workspaces/{ws2}/ai/status", Json))!;
            Assert.Equal((false, true), (off.Enabled, off.Allowed));
            var refused = await pro2.PostAsJsonAsync($"/api/workspaces/{ws2}/ai/posts", body, Json);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("ยังไม่ได้ตั้งค่า", await TitleAsync(refused));
        }
        finally
        {
            factory.Ai.IsOn = true;
        }
    }

    // ---------- bumping ----------

    [Fact]
    public async Task A_bumping_schedule_makes_the_browser_comment_on_its_posts_later()
    {
        using var shop = await factory.ShopAsync("agency", links: 1, posts: 1);
        var image = await shop.Owner.UploadImageAsync(shop.Ws);
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(
            Mode: "once", StartNow: true, BumpHours: 2,
            Bump: new { rounds = 2, text = "{ดันค่ะ|ขึ้นๆ}", mediaIds = new[] { image.Id }, imagesEach = 1 }));
        Assert.Equal((2, 2, 1), (created.Schedule.BumpHours, created.Schedule.Bump!.Rounds, created.Schedule.Bump.ImagesEach));
        shop.WaitUntil(created.LastAt!.Value);

        // The post goes out and the extension says where it is on Facebook.
        var job = (await shop.ClaimAsync())!;
        Assert.Equal("post", job.Kind);
        const string postUrl = "https://www.facebook.com/groups/shop0/posts/123456789/";
        await shop.ReportAsync(job.PostId, postUrl: postUrl);
        var bumps = await factory.WithDbAsync(db => db.PostBumps.Where(b => b.WorkspaceId == shop.Ws).OrderBy(b => b.Round).ToListAsync());
        Assert.Equal([1, 2], bumps.Select(b => b.Round));
        Assert.All(bumps, b => Assert.Equal((BumpStatus.Queued, postUrl, 1), (b.Status, b.Url, b.MediaIds.Count)));
        Assert.All(bumps, b => Assert.Contains(b.Text, new[] { "ดันค่ะ", "ขึ้นๆ" }));

        // Nothing is due until the bump hours have passed.
        Assert.Null(await shop.ClaimAsync());

        shop.Wait(TimeSpan.FromHours(2.5));
        var bump = (await shop.ClaimAsync())!;
        Assert.Equal(("bump", postUrl, 1), (bump.Kind, bump.GroupUrl, bump.Media.Count));
        Assert.Contains(bump.Content, new[] { "ดันค่ะ", "ขึ้นๆ" });
        Assert.Null(await shop.ClaimAsync()); // one job at a time: the bump has not been reported

        Assert.Equal(HttpStatusCode.NoContent, (await shop.ReportBumpAsync(bump.PostId)).StatusCode);
        Assert.Null(await shop.ClaimAsync()); // the second one waits for its own time

        shop.Wait(TimeSpan.FromHours(2));
        var second = (await shop.ClaimAsync())!;
        Assert.Equal("bump", second.Kind);
        Assert.Equal(HttpStatusCode.NoContent, (await shop.ReportBumpAsync(second.PostId, ok: false, error: "ไม่พบช่องคอมเมนต์")).StatusCode);

        var done = await factory.WithDbAsync(db => db.PostBumps.Where(b => b.WorkspaceId == shop.Ws).OrderBy(b => b.Round).ToListAsync());
        Assert.Equal([BumpStatus.Done, BumpStatus.Failed], done.Select(b => b.Status));
        Assert.Equal("ไม่พบช่องคอมเมนต์", done[1].FailureDetail);

        // A bump is not a post: it is not in the calendar, the counts or the errors.
        Assert.Single(await shop.PostsAsync(created.Schedule.Id));
    }

    [Fact]
    public async Task No_bump_is_made_without_the_posts_address_for_an_owner_without_the_feature_or_for_a_test()
    {
        var admin = await factory.AdminAsync(); // signed in before the clock moves
        using var shop = await factory.ShopAsync("agency", links: 2, posts: 1);
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartNow: true, BumpHours: 1));
        shop.WaitUntil(created.LastAt!.Value);

        // The extension could not read the address: nothing to bump.
        var job = (await shop.ClaimAsync())!;
        await shop.ReportAsync(job.PostId);
        Assert.Empty(await factory.WithDbAsync(db => db.PostBumps.Where(b => b.WorkspaceId == shop.Ws).ToListAsync()));

        // The owner drops to a plan without bumping: the next post of the schedule makes none, address or not.
        (await admin.PutAsJsonAsync($"/api/admin/customers/{shop.Auth.User.Id}/plan", new { plan = "pro" }, Json)).EnsureSuccessStatusCode();
        shop.Wait(TimeSpan.FromMinutes(15));
        var second = (await shop.ClaimAsync())!;
        await shop.ReportAsync(second.PostId, postUrl: "https://www.facebook.com/groups/x/posts/2/");
        Assert.Empty(await factory.WithDbAsync(db => db.PostBumps.Where(b => b.WorkspaceId == shop.Ws).ToListAsync()));

        // A test post is never bumped.
        shop.Wait(TimeSpan.FromMinutes(15));
        var test = await shop.TestPostAsync();
        var testJob = (await shop.ClaimAsync())!;
        Assert.Equal(test.Id, testJob.PostId);
        await shop.ReportAsync(testJob.PostId, postUrl: "https://www.facebook.com/groups/x/posts/1/");
        Assert.Empty(await factory.WithDbAsync(db => db.PostBumps.Where(b => b.WorkspaceId == shop.Ws).ToListAsync()));
    }
}
