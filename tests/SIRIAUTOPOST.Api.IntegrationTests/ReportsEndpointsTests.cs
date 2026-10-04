using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Reports: real posts only, per link and per collection post, 7 or 30 days; client report links (Agency).
[Collection(ApiCollection.Name)]
public partial class ReportsEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenShape();

    private static string Reports(Guid ws, string query = "") => $"/api/workspaces/{ws}/reports{query}";

    private static async Task AssertProblemAsync(HttpResponseMessage res, HttpStatusCode status, string? title = null)
    {
        Assert.Equal(status, res.StatusCode);
        if (title is not null) Assert.Equal(title, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title);
    }

    private static async Task<ReportDto> ReportAsync(HttpClient client, Guid ws, int? days = null) =>
        (await client.GetFromJsonAsync<ReportDto>(Reports(ws, days is null ? "" : $"?days={days}"), Json))!;

    // ---- the world the numbers are checked in ----

    private sealed class World
    {
        public required Guid Ws { get; init; }
        public required Guid Device { get; init; }
        public required Guid Account { get; init; }
        public required SetLinkDto L1 { get; init; }
        public required SetLinkDto L2 { get; init; }
        public required SetLinkDto Quiet { get; init; }
        public required CollectionPostDto Cp1 { get; init; }
        public required CollectionPostDto Cp2 { get; init; }
        public required CollectionPostDto Cp3 { get; init; }
    }

    private sealed class Seeder(Guid ws, SocialAccount account, Guid device, DateTimeOffset now)
    {
        private readonly Guid schedule = Guid.NewGuid();
        private int counter;
        public List<Post> Posts { get; } = [];

        /// <param name="state">success, failed, pending, skipped, queued or test.</param>
        public Post Add(Guid? link, string target, string? url, Guid cp, string state, double hours)
        {
            var at = now.AddHours(hours);
            var key = link is { } l ? Post.LinkTargetKey(l) : Post.AccountTargetKey(account.Id);
            Post p;
            if (state == "test")
            {
                p = Post.Test(ws, account, target, "ทดสอบ", [], at, cp, link, url, null);
                p.Claim(device, at);
                p.CompletePosted(false, at);
            }
            else
            {
                p = Post.FromSchedule(ws, account, target, "ข้อความ", [], at, now, schedule, cp, link, key + ":" + target, $"slot{counter++}", url, null);
                switch (state)
                {
                    case "success": p.Claim(device, at); p.CompletePosted(false, at); break;
                    case "pending": p.Claim(device, at); p.CompletePosted(true, at); break;
                    case "failed": p.Claim(device, at); p.Fail(FailureCode.Network, "x", at); break;
                    case "skipped": p.Skip(at, "x"); break;
                }
            }
            Posts.Add(p);
            return p;
        }

        /// <summary>A post that never came from a link or the collections (the old composer, the extension's history).</summary>
        public void Record(string target, PostStatus status, double hours) =>
            Posts.Add(Post.Record(ws, account, target, "เก่า", now.AddHours(hours), status, status == PostStatus.Failed ? FailureCode.Network : null, now));
    }

    /// <summary>
    /// A workspace with a paired browser, a link set of three groups (L1, L2 and one that never posted), three collection
    /// posts and the posts below (hours are relative to now; the report asks for the last 7 days unless noted):
    /// L1: ok cp1 -1h, -2d, -3d; ok cp2 -6d (four in all); failed -3h; awaiting approval -5h; skipped -4h; queued +1h; a test ok -1h;
    ///     ok cp1 -10d (30 days only); ok cp1 -40d (never).
    /// L2: ok cp2 -1d; ok cp3 -2d; ok cp1 -3d; failed cp3 -1d.
    /// a deleted link "กลุ่มที่ถูกลบ": ok cp3 -1d and -2d.   Without a link: "กลุ่มเก่า" ok -2d; "กลุ่มรออนุมัติ" awaiting approval -1h.
    /// </summary>
    private async Task<World> BuildAsync(HttpClient owner, Guid ws)
    {
        var (_, pair) = await factory.PairDeviceAsync(owner, ws);
        var set = await owner.CreateLinkSetAsync(ws, "ชุดร้านค้า");
        var l1 = await owner.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/a-shop", "A1", "กลุ่มร้านค้า");
        var l2 = await owner.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/b-shop", "B1", "กลุ่มขายส่ง");
        var quiet = await owner.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/quiet", "Q1", "กลุ่มเงียบ");
        var collection = await owner.CreateCollectionAsync(ws);
        var (cp1, cp2, cp3) = (await owner.AddPostAsync(ws, collection.Id, "โพสต์หนึ่ง"), await owner.AddPostAsync(ws, collection.Id, "โพสต์สอง"), await owner.AddPostAsync(ws, collection.Id, "โพสต์สาม"));
        var world = new World { Ws = ws, Device = pair.DeviceId, Account = pair.AccountId, L1 = l1, L2 = l2, Quiet = quiet, Cp1 = cp1, Cp2 = cp2, Cp3 = cp3 };

        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == pair.AccountId);
            var s = new Seeder(ws, account, pair.DeviceId, DateTimeOffset.UtcNow);
            const string A = "กลุ่มร้านค้า", B = "กลุ่มขายส่ง";
            var (u1, u2) = ("https://www.facebook.com/groups/a-shop", "https://www.facebook.com/groups/b-shop");
            s.Add(l1.Id, A, u1, cp1.Id, "success", -1);
            s.Add(l1.Id, A, u1, cp1.Id, "success", -48);
            s.Add(l1.Id, A, u1, cp1.Id, "success", -72);
            s.Add(l1.Id, A, u1, cp2.Id, "success", -144);
            s.Add(l1.Id, A, u1, cp1.Id, "failed", -3);
            s.Add(l1.Id, A, u1, cp2.Id, "pending", -5);
            s.Add(l1.Id, A, u1, cp1.Id, "skipped", -4);
            s.Add(l1.Id, A, u1, cp3.Id, "queued", 1);
            s.Add(l1.Id, A, u1, cp3.Id, "test", -1);
            s.Add(l1.Id, A, u1, cp1.Id, "success", -240);
            s.Add(l1.Id, A, u1, cp1.Id, "success", -960);
            s.Add(l2.Id, B, u2, cp2.Id, "success", -24);
            s.Add(l2.Id, B, u2, cp3.Id, "success", -48);
            s.Add(l2.Id, B, u2, cp1.Id, "success", -72);
            s.Add(l2.Id, B, u2, cp3.Id, "failed", -24);
            var gone = Guid.NewGuid();
            s.Add(gone, "กลุ่มที่ถูกลบ", "https://www.facebook.com/groups/gone", cp3.Id, "success", -24);
            s.Add(gone, "กลุ่มที่ถูกลบ", "https://www.facebook.com/groups/gone", cp3.Id, "success", -48);
            s.Record("กลุ่มเก่า", PostStatus.Success, -48);
            s.Record("กลุ่มรออนุมัติ", PostStatus.Pending, -1);
            db.Posts.AddRange(s.Posts);
            await db.SaveChangesAsync();
        });
        return world;
    }

    // ---- numbers ----

    [Fact]
    public async Task Groups_are_counted_per_link_for_the_last_seven_days()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var w = await BuildAsync(client, ws);

        var report = await ReportAsync(client, ws); // days defaults to 7

        Assert.Equal(7, report.Days);
        Assert.InRange((report.To - DateTimeOffset.UtcNow).TotalSeconds, -30, 30);
        Assert.Equal(TimeSpan.FromDays(7), report.To - report.From);
        // The demo accounts of a new workspace have a week of history; none of it counts (no browser posted it).
        Assert.Equal(["กลุ่มร้านค้า", "กลุ่มขายส่ง", "กลุ่มที่ถูกลบ", "กลุ่มเก่า", "กลุ่มรออนุมัติ"], report.Groups.Select(g => g.Name));

        var l1 = report.Groups[0];
        Assert.Equal((w.L1.Id, "https://www.facebook.com/groups/a-shop", Platform.Fb), (l1.LinkId, l1.Url, l1.Platform));
        Assert.Equal((4, 1, 1, 80), (l1.Posted, l1.Pending, l1.Failed, l1.Rate)); // ok x4, awaiting approval x1, failed x1; skipped, queued, a test and the old ones left out
        var l2 = report.Groups[1];
        Assert.Equal((w.L2.Id, 3, 0, 1, 75), (l2.LinkId, l2.Posted, l2.Pending, l2.Failed, l2.Rate));
        var ghost = report.Groups[2];
        Assert.Equal((null, "https://www.facebook.com/groups/gone", 2, 0, 0, 100, true, LinkHealth.Ok), (ghost.LinkId, ghost.Url, ghost.Posted, ghost.Pending, ghost.Failed, ghost.Rate, ghost.Enabled, ghost.Health));
        var old = report.Groups[3];
        Assert.Equal((null, null, 1), (old.LinkId, old.Url, old.Posted));
        var waiting = report.Groups[4];
        Assert.Equal((0, 1, 0, 100), (waiting.Posted, waiting.Pending, waiting.Failed, waiting.Rate)); // only awaiting approval: nothing failed
        Assert.DoesNotContain(report.Groups, g => g.LinkId == w.Quiet.Id); // a group with nothing in the period is not listed
    }

    [Fact]
    public async Task Thirty_days_reaches_further_back()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var w = await BuildAsync(client, ws);

        var report = await ReportAsync(client, ws, 30);

        Assert.Equal(30, report.Days);
        Assert.Equal(TimeSpan.FromDays(30), report.To - report.From);
        var l1 = report.Groups.Single(g => g.LinkId == w.L1.Id);
        Assert.Equal((5, 1, 1, 83), (l1.Posted, l1.Pending, l1.Failed, l1.Rate)); // plus the one from 10 days ago, not the one from 40
        Assert.Equal(75, report.Groups.Single(g => g.LinkId == w.L2.Id).Rate);
    }

    [Fact]
    public async Task The_posts_section_ranks_collection_posts_by_successful_posts_in_the_period()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var w = await BuildAsync(client, ws);

        var seven = await ReportAsync(client, ws, 7);
        // cp1: L1 x3 + L2 = 4; cp3: L2, and the deleted link x2 = 3; cp2: L1 and L2 = 2. (The test, failed, pending, skipped and queued posts do not count.)
        Assert.Equal([(w.Cp1.Id, "โพสต์หนึ่ง", 4), (w.Cp3.Id, "โพสต์สาม", 3), (w.Cp2.Id, "โพสต์สอง", 2)], seven.Posts.Select(p => (p.CollectionPostId, p.Text, p.Used)));

        var thirty = await ReportAsync(client, ws, 30);
        Assert.Equal((w.Cp1.Id, 5), (thirty.Posts[0].CollectionPostId, thirty.Posts[0].Used)); // the post from 10 days ago
    }

    [Fact]
    public async Task Only_the_top_twenty_posts_are_listed_and_deleted_ones_are_skipped()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var collection = await client.CreateCollectionAsync(ws);
        var posts = new List<CollectionPostDto>();
        for (var i = 1; i <= 50; i++) posts.Add(await client.AddPostAsync(ws, collection.Id, $"โพสต์ {i}")); // post i is used i times
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == pair.AccountId);
            var s = new Seeder(ws, account, pair.DeviceId, DateTimeOffset.UtcNow);
            for (var i = 1; i <= 50; i++)
                for (var k = 0; k < i; k++) s.Add(null, "กลุ่ม", null, posts[i - 1].Id, "success", -1 - k * 0.01);
            db.Posts.AddRange(s.Posts);
            await db.SaveChangesAsync();
        });

        var top = await ReportAsync(client, ws);
        Assert.Equal(Enumerable.Range(31, 20).Reverse(), top.Posts.Select(p => p.Used)); // 50 down to 31
        Assert.Equal("โพสต์ 50", top.Posts[0].Text);

        // Delete the 25 most used: the ranking reaches past the first candidates to still find twenty that exist.
        foreach (var p in posts.Skip(25)) (await client.DeleteAsync($"/api/workspaces/{ws}/collections/{collection.Id}/posts/{p.Id}")).EnsureSuccessStatusCode();
        var rest = await ReportAsync(client, ws);
        Assert.Equal(Enumerable.Range(6, 20).Reverse(), rest.Posts.Select(p => p.Used)); // 25 down to 6
        Assert.Equal("โพสต์ 25", rest.Posts[0].Text);
    }

    [Fact]
    public async Task A_links_current_state_is_reported()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var w = await BuildAsync(client, ws);
        (await client.PutAsJsonAsync($"/api/workspaces/{ws}/link-sets/{(await client.LinkSetsAsync(ws)).Single().Id}/links/{w.L2.Id}",
            new { name = "กลุ่มขายส่ง", url = "https://www.facebook.com/groups/b-shop", code = "B1", dailyMax = 0, enabled = false }, Json)).EnsureSuccessStatusCode();
        await factory.WithDbAsync(async db =>
        {
            (await db.SetLinks.SingleAsync(l => l.Id == w.L1.Id)).RecordPendingApproval();
            await db.SaveChangesAsync();
        });

        var report = await ReportAsync(client, ws);

        var l1 = report.Groups.Single(g => g.LinkId == w.L1.Id);
        Assert.Equal((true, LinkHealth.Pending), (l1.Enabled, l1.Health));
        var l2 = report.Groups.Single(g => g.LinkId == w.L2.Id);
        Assert.Equal((false, LinkHealth.Off, 3), (l2.Enabled, l2.Health, l2.Posted)); // switched off, its past posts still count
    }

    [Fact]
    public async Task Another_workspaces_posts_and_a_fresh_workspace_give_an_empty_report()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        await BuildAsync(client, ws);
        var (other, _, otherWs) = await factory.SignUpAsync();

        var report = await ReportAsync(other, otherWs);

        Assert.Empty(report.Groups); // the sample accounts' history never went out
        Assert.Empty(report.Posts);
    }

    // ---- who may ask, and what ----

    [Fact]
    public async Task Only_seven_or_thirty_days_can_be_asked_for()
    {
        var (client, _, ws) = await factory.SignUpAsync();

        foreach (var days in new[] { "0", "1", "14", "8", "-7", "90", "abc", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Reports(ws, $"?days={days}"))).StatusCode);
        var problem = await (await client.GetAsync(Reports(ws, "?days=14"))).Content.ReadFromJsonAsync<ValidationProblemDetails>(Json);
        Assert.Contains("days", problem!.Errors.Keys);
        Assert.Equal(7, (await ReportAsync(client, ws, 7)).Days);
        Assert.Equal(30, (await ReportAsync(client, ws, 30)).Days);
    }

    [Fact]
    public async Task Everyone_in_the_workspace_reads_the_report_and_strangers_do_not()
    {
        var team = await factory.TeamAsync();

        foreach (var client in new[] { team.Viewer, team.Editor, team.Admin, team.Owner })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Reports(team.Ws))).StatusCode);

        var (stranger, _, _) = await factory.SignUpAsync("agency");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Reports(team.Ws))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Reports(team.Ws))).StatusCode);
    }

    // ---- client report links ----

    private static object Share(string brand = "ร้านสมชาย", string period = "week", bool logo = true) => new { brand, period, logo };

    private async Task<ReportShareDto> ShareAsync(HttpClient client, Guid ws, object? body = null) =>
        await (await client.PostAsJsonAsync(Reports(ws, "/share"), body ?? Share(), Json)).ReadAsync<ReportShareDto>();

    private async Task<HttpResponseMessage> OpenAsync(string token) => await factory.CreateClient().GetAsync($"/api/reports/shared/{token}");

    [Fact]
    public async Task A_shared_report_is_a_frozen_copy_anyone_with_the_link_can_open()
    {
        var team = await factory.TeamAsync(); // Agency
        var w = await BuildAsync(team.Owner, team.Ws);
        var live = await ReportAsync(team.Owner, team.Ws);

        var share = await ShareAsync(team.Admin, team.Ws, Share("  ร้านสมชาย  ", "week", true));

        Assert.Matches(TokenShape(), share.Token);
        Assert.Equal("/report/" + share.Token, share.Path);
        Assert.InRange((share.ExpiresAt - DateTimeOffset.UtcNow).TotalDays, 29.99, 30.01);

        var res = await OpenAsync(share.Token); // no sign-in
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var shared = (await res.Content.ReadFromJsonAsync<SharedReportDto>(Json))!;
        var workspaceName = (await team.Owner.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single(x => x.Id == team.Ws).Name;
        Assert.Equal(("ร้านสมชาย", workspaceName, true, "week"), (shared.Brand, shared.WorkspaceName, shared.Logo, shared.Period));
        Assert.InRange((shared.CreatedAt - DateTimeOffset.UtcNow).TotalSeconds, -30, 30);
        Assert.Equal(share.ExpiresAt, shared.ExpiresAt);
        Assert.Equal(7, shared.Report.Days);
        Assert.Equal(live.Groups.Select(g => (g.Name, g.Posted, g.Pending, g.Failed, g.Rate)), shared.Report.Groups.Select(g => (g.Name, g.Posted, g.Pending, g.Failed, g.Rate)));
        Assert.Equal(live.Posts.Select(p => (p.CollectionPostId, p.Text, p.Used)), shared.Report.Posts.Select(p => (p.CollectionPostId, p.Text, p.Used)));
        Assert.Equal(w.L1.Id, shared.Report.Groups[0].LinkId);
        Assert.Equal(Platform.Fb, shared.Report.Groups[0].Platform);
        Assert.Equal(LinkHealth.Ok, shared.Report.Groups[0].Health);

        // The copy is frozen: more posts later change the live report, not the shared one.
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == w.Account);
            var s = new Seeder(team.Ws, account, w.Device, DateTimeOffset.UtcNow);
            s.Add(w.L1.Id, "กลุ่มร้านค้า", null, w.Cp2.Id, "success", -0.5);
            db.Posts.AddRange(s.Posts);
            await db.SaveChangesAsync();
        });
        Assert.Equal(5, (await ReportAsync(team.Owner, team.Ws)).Groups[0].Posted);
        var again = (await (await OpenAsync(share.Token)).Content.ReadFromJsonAsync<SharedReportDto>(Json))!;
        Assert.Equal(4, again.Report.Groups[0].Posted);

        // Nothing about the workspace beyond the report leaks into the shared copy.
        var raw = await (await OpenAsync(share.Token)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(team.Ws.ToString(), raw);
        Assert.DoesNotContain("owner", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_monthly_share_covers_thirty_days_and_can_leave_the_logo_out()
    {
        var team = await factory.TeamAsync();
        await BuildAsync(team.Owner, team.Ws);

        var share = await ShareAsync(team.Owner, team.Ws, Share("แบรนด์", "month", logo: false));

        var shared = (await (await OpenAsync(share.Token)).Content.ReadFromJsonAsync<SharedReportDto>(Json))!;
        Assert.Equal(("month", false, 30), (shared.Period, shared.Logo, shared.Report.Days));
        Assert.Equal(5, shared.Report.Groups[0].Posted); // the 30-day numbers
        // An empty workspace shares an empty report.
        var (agency, _, ws) = await factory.SignUpAsync("agency");
        var empty = (await (await OpenAsync((await ShareAsync(agency, ws)).Token)).Content.ReadFromJsonAsync<SharedReportDto>(Json))!;
        Assert.Empty(empty.Report.Groups);
    }

    [Fact]
    public async Task Unknown_malformed_and_expired_links_are_all_a_404()
    {
        var (agency, _, ws) = await factory.SignUpAsync("agency");
        var share = await ShareAsync(agency, ws);

        Assert.Equal(HttpStatusCode.OK, (await OpenAsync(share.Token)).StatusCode);
        foreach (var token in new[] { new string('a', 43), "short", new string('a', 100), new string('A', 42) + "+", "%20" + new string('a', 40) })
            Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(token)).StatusCode);

        using (factory.Clock.Advance(TimeSpan.FromDays(29)))
            Assert.Equal(HttpStatusCode.OK, (await OpenAsync(share.Token)).StatusCode); // still alive on day 29
        using (factory.Clock.Advance(TimeSpan.FromDays(31)))
            Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(share.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenAsync(share.Token)).StatusCode); // the clock is back
    }

    [Fact]
    public async Task Sharing_needs_the_Agency_plan_and_an_admin()
    {
        foreach (var plan in new string?[] { null, "basic", "pro" })
        {
            var (client, _, ws) = await factory.SignUpAsync(plan);
            await AssertProblemAsync(await client.PostAsJsonAsync(Reports(ws, "/share"), Share(), Json), HttpStatusCode.Forbidden, "ต้องใช้แผน Agency");
        }

        var team = await factory.TeamAsync();
        foreach (var client in new[] { team.Viewer, team.Editor })
            await AssertProblemAsync(await client.PostAsJsonAsync(Reports(team.Ws, "/share"), Share(), Json), HttpStatusCode.Forbidden, "สิทธิ์ของคุณในเวิร์กสเปซนี้ทำรายการนี้ไม่ได้");
        Assert.Matches(TokenShape(), (await ShareAsync(team.Admin, team.Ws)).Token); // the owner's plan counts, whoever asks
        Assert.Matches(TokenShape(), (await ShareAsync(team.Owner, team.Ws)).Token);

        var (stranger, _, _) = await factory.SignUpAsync("agency");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(Reports(team.Ws, "/share"), Share(), Json)).StatusCode);
    }

    [Fact]
    public async Task The_brand_is_required_and_the_period_is_week_or_month()
    {
        var (agency, _, ws) = await factory.SignUpAsync("agency");

        foreach (var brand in new[] { "", "   ", new string('ก', 121) })
            await AssertProblemAsync(await agency.PostAsJsonAsync(Reports(ws, "/share"), Share(brand), Json), HttpStatusCode.BadRequest);
        foreach (var period in new[] { "year", "", "WEEK", "7" })
            await AssertProblemAsync(await agency.PostAsJsonAsync(Reports(ws, "/share"), Share(period: period), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await agency.PostAsJsonAsync(Reports(ws, "/share"), new { brand = "x", logo = true }, Json), HttpStatusCode.BadRequest);
        Assert.Matches(TokenShape(), (await ShareAsync(agency, ws, Share(new string('ก', 120)))).Token); // the limit itself is fine
    }

    [Fact]
    public async Task A_workspace_keeps_at_most_twenty_live_links_and_expired_ones_make_room()
    {
        var (agency, _, ws) = await factory.SignUpAsync("agency");
        var tokens = new List<string>();
        for (var i = 0; i < 20; i++) tokens.Add((await ShareAsync(agency, ws, Share($"แบรนด์ {i}"))).Token);
        Assert.Equal(20, tokens.Distinct().Count());

        await AssertProblemAsync(await agency.PostAsJsonAsync(Reports(ws, "/share"), Share(), Json), HttpStatusCode.UnprocessableEntity);
        Assert.Equal(20, await factory.WithDbAsync(db => db.ReportShares.CountAsync(x => x.WorkspaceId == ws)));

        // A month later they have all expired: the next link clears them out and starts again.
        using (factory.Clock.Advance(TimeSpan.FromDays(31)))
        {
            var fresh = await ShareAsync(agency, ws);
            Assert.Equal(HttpStatusCode.OK, (await OpenAsync(fresh.Token)).StatusCode);
            Assert.Equal(1, await factory.WithDbAsync(db => db.ReportShares.CountAsync(x => x.WorkspaceId == ws)));
            Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync(tokens[0])).StatusCode);
        }

        // Another workspace has its own allowance.
        var (other, _, otherWs) = await factory.SignUpAsync("agency");
        Assert.Matches(TokenShape(), (await ShareAsync(other, otherWs)).Token);
    }
}
