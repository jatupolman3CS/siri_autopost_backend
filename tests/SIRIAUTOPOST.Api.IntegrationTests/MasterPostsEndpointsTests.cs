using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.MasterPosts;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The post library ("คลังโพสต์") through the real API: posts that manage themselves and sit in several collections.
[Collection(ApiCollection.Name)]
public class MasterPostsEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;

    private static string Lib(Guid ws) => $"/api/workspaces/{ws}/master-posts";

    private static async Task<CollectionPostDto> CreateAsync(
        HttpClient client, Guid ws, string text = "โพสต์ในคลัง", Guid[]? collections = null, object? settings = null, bool? active = null) =>
        await (await client.PostAsJsonAsync(Lib(ws), new { text, mediaIds = Array.Empty<Guid>(), collectionIds = collections, settings, active }, Json))
            .ReadAsync<CollectionPostDto>();

    private static async Task<List<CollectionPostDto>> ListAsync(HttpClient client, Guid ws) =>
        (await client.GetFromJsonAsync<List<CollectionPostDto>>(Lib(ws), Json))!;

    private static object Own(
        string? hashtags = null, string? footer = null, string? footerPos = null, string? validFrom = null, string? validUntil = null,
        int[]? weekdays = null, string? timeFrom = null, string? timeTo = null, int maxPerDay = 0) =>
        new { hashtags, footer, footerPos, validFrom, validUntil, weekdays = weekdays ?? [], timeFrom, timeTo, maxPerDay };

    [Fact]
    public async Task A_post_can_wait_in_the_library_and_sit_in_several_collections()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        Assert.Empty(await ListAsync(client, ws));
        var a = await client.CreateCollectionAsync(ws, "A");
        var b = await client.CreateCollectionAsync(ws, "B");

        var waiting = await CreateAsync(client, ws, "  รอในคลัง  ");
        Assert.Equal(("รอในคลัง", true, PostApproval.Approved), (waiting.Text, waiting.Active, waiting.Approval));
        Assert.Empty(waiting.CollectionIds);
        Assert.Equal((0, 0, 0), (waiting.PostedCount, waiting.QueuedCount, waiting.FailedCount));

        var both = await CreateAsync(client, ws, "อยู่สองชุด", [a.Id, b.Id]);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), both.CollectionIds.Order());

        var collections = await client.CollectionsAsync(ws);
        Assert.Equal([both.Id], collections.Single(c => c.Id == a.Id).Posts.Select(p => p.Id));
        Assert.Equal([both.Id], collections.Single(c => c.Id == b.Id).Posts.Select(p => p.Id));
        Assert.Equal([waiting.Id, both.Id], (await ListAsync(client, ws)).Select(p => p.Id));

        // Put the waiting one into A through the collection's own endpoint; the one already in it stays as it is.
        var added = await (await client.PostAsJsonAsync($"/api/workspaces/{ws}/collections/{a.Id}/posts/add", new { postIds = new[] { waiting.Id, both.Id } }, Json))
            .ReadAsync<CollectionDto>();
        Assert.Equal([waiting.Id, both.Id], added.Posts.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync($"/api/workspaces/{ws}/collections/{a.Id}/posts/add", new { postIds = new[] { Guid.NewGuid() } }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_posts_form_changes_its_text_settings_and_collections_and_null_keeps_them()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var a = await client.CreateCollectionAsync(ws, "A");
        var b = await client.CreateCollectionAsync(ws, "B");
        var post = await CreateAsync(client, ws, "ก่อนแก้", [a.Id]);

        var changed = await (await client.PutAsJsonAsync($"{Lib(ws)}/{post.Id}", new
        {
            text = "หลังแก้", mediaIds = Array.Empty<Guid>(), collectionIds = new[] { b.Id },
            settings = Own(hashtags: "#โปร", footer: "", footerPos: "top", weekdays: [5, 1], timeFrom: "18:00", timeTo: "21:00", maxPerDay: 2,
                validFrom: "2026-10-05", validUntil: "2026-12-31"),
        }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal("หลังแก้", changed.Text);
        Assert.Equal([b.Id], changed.CollectionIds);
        Assert.Equal(("#โปร", "", FooterPosition.Top, "2026-10-05", "2026-12-31", "18:00", "21:00", 2),
            (changed.Settings.Hashtags, changed.Settings.Footer, changed.Settings.FooterPos, changed.Settings.ValidFrom, changed.Settings.ValidUntil, changed.Settings.TimeFrom, changed.Settings.TimeTo, changed.Settings.MaxPerDay));
        Assert.Equal([1, 5], changed.Settings.Weekdays);

        var kept = await (await client.PutAsJsonAsync($"{Lib(ws)}/{post.Id}", new { text = "หลังแก้ 2", mediaIds = Array.Empty<Guid>() }, Json))
            .ReadAsync<CollectionPostDto>();
        Assert.Equal([b.Id], kept.CollectionIds);
        Assert.Equal(2, kept.Settings.MaxPerDay);

        // A post with no collection at all is allowed: it goes back to waiting in the library.
        var none = await (await client.PutAsJsonAsync($"{Lib(ws)}/{post.Id}", new { text = "รอ", collectionIds = Array.Empty<Guid>() }, Json))
            .ReadAsync<CollectionPostDto>();
        Assert.Empty(none.CollectionIds);
        Assert.Empty((await client.CollectionsAsync(ws)).SelectMany(c => c.Posts));
    }

    [Fact]
    public async Task A_posts_own_settings_are_checked()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var post = await CreateAsync(client, ws);

        async Task<HttpStatusCode> Try(object settings) =>
            (await client.PutAsJsonAsync($"{Lib(ws)}/{post.Id}", new { text = "x", settings }, Json)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Try(Own(maxPerDay: 99)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Own(timeFrom: "18:00")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Own(timeFrom: "25:00", timeTo: "26:00")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Own(weekdays: [9])));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Own(validFrom: "2026-10-05", validUntil: "2026-10-01")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(Own(validFrom: "5 ตุลา")));
        Assert.Equal(HttpStatusCode.OK, await Try(Own(timeFrom: "22:00", timeTo: "02:00", weekdays: [0, 6])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync(Lib(ws), new { text = "x", collectionIds = new[] { Guid.NewGuid() } }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Lib(ws)}/{Guid.NewGuid()}", new { text = "x" }, Json)).StatusCode);
    }

    [Fact]
    public async Task Switching_a_post_off_takes_what_it_queued_out_and_on_makes_it_again()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        var first = shop.Posts[0];
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: 420));
        Assert.Contains(await shop.PostsAsync(created.Schedule.Id), p => p.CollectionPostId == first.Id);

        var off = await (await shop.Owner.PutAsJsonAsync($"{Lib(shop.Ws)}/{first.Id}/active", new { active = false }, Json)).ReadAsync<CollectionPostDto>();
        Assert.False(off.Active);
        var without = await shop.PostsAsync(created.Schedule.Id);
        Assert.Equal(created.Created, without.Count); // the slots it left were filled again by the other post
        Assert.DoesNotContain(without, p => p.CollectionPostId == first.Id);
        Assert.Equal(1, (await shop.SchedulesAsync()).Single().UsablePosts);

        var on = await (await shop.Owner.PutAsJsonAsync($"{Lib(shop.Ws)}/{first.Id}/active", new { active = true }, Json)).ReadAsync<CollectionPostDto>();
        Assert.True(on.Active);
        Assert.Equal(2, (await shop.SchedulesAsync()).Single().UsablePosts);
        Assert.Equal(created.Created, (await shop.PostsAsync(created.Schedule.Id)).Count); // the slots are full: it comes back in the slots made later
    }

    [Fact]
    public async Task Deleting_a_post_removes_it_from_every_collection_and_its_queued_posts()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        var other = await shop.Owner.CreateCollectionAsync(shop.Ws, "ชุดอื่น");
        var doomed = shop.Posts[0];
        (await shop.Owner.PostAsJsonAsync($"{shop.Api}/collections/{other.Id}/posts/add", new { postIds = new[] { doomed.Id } }, Json)).EnsureSuccessStatusCode();
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: 420));
        Assert.Contains(await shop.PostsAsync(created.Schedule.Id), p => p.CollectionPostId == doomed.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await shop.Owner.DeleteAsync($"{Lib(shop.Ws)}/{doomed.Id}")).StatusCode);

        var collections = await shop.Owner.CollectionsAsync(shop.Ws);
        Assert.All(collections, c => Assert.DoesNotContain(c.Posts, p => p.Id == doomed.Id));
        Assert.DoesNotContain(await shop.PostsAsync(created.Schedule.Id), p => p.CollectionPostId == doomed.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await shop.Owner.DeleteAsync($"{Lib(shop.Ws)}/{doomed.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_post_taken_out_of_a_collection_loses_what_that_collections_schedule_queued_for_it()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        var leaving = shop.Posts[0];
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: 420));
        Assert.Contains(await shop.PostsAsync(created.Schedule.Id), p => p.CollectionPostId == leaving.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await shop.Owner.DeleteAsync($"{shop.Api}/collections/{shop.Collection.Id}/posts/{leaving.Id}")).StatusCode);

        Assert.DoesNotContain(await shop.PostsAsync(created.Schedule.Id), p => p.CollectionPostId == leaving.Id);
        Assert.Contains(await ListAsync(shop.Owner, shop.Ws), p => p.Id == leaving.Id); // still in the library
    }

    [Fact]
    public async Task A_post_composes_with_its_own_footer_and_hashtags_in_the_schedule()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 0, footer: "ท้ายชุด");
        var own = await CreateAsync(shop.Owner, shop.Ws, "สินค้าใหม่", [shop.Collection.Id], Own(footer: "ท้ายของโพสต์", hashtags: "#เฉพาะโพสต์"));
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));

        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: 420));

        var posts = await shop.PostsAsync(created.Schedule.Id);
        Assert.All(posts, p => Assert.EndsWith("สินค้าใหม่\n\nท้ายของโพสต์\n#เฉพาะโพสต์", p.Content));
        Assert.All(posts, p => Assert.DoesNotContain("ท้ายชุด", p.Content));
        Assert.Equal(own.Id, posts[0].CollectionPostId);
    }

    [Fact]
    public async Task A_post_limited_to_other_hours_than_the_slot_is_not_made_and_the_schedule_says_so()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 0);
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        var hour = int.Parse(slot[..2]);
        var elsewhere = $"{(hour + 6) % 24:00}:00";
        await CreateAsync(shop.Owner, shop.Ws, "ช่วงอื่น", [shop.Collection.Id], Own(timeFrom: elsewhere, timeTo: elsewhere));

        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: 420));

        Assert.Equal(0, created.Created);
    }

    [Fact]
    public async Task What_a_post_made_shows_on_its_own_card()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 1);
        var (slot, _) = SlotAhead(shop.Now, 420, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: 420));
        var post = shop.Posts[0];

        var card = (await ListAsync(shop.Owner, shop.Ws)).Single(p => p.Id == post.Id);
        Assert.Equal((created.Created, 0, 0), (card.QueuedCount, card.PostedCount, card.FailedCount));
        Assert.Equal(created.FirstAt, card.NextAt);

        var activity = (await shop.Owner.GetFromJsonAsync<List<CollectionPostActivityDto>>($"{Lib(shop.Ws)}/{post.Id}/activity?take=5", Json))!;
        Assert.Equal(Math.Min(5, created.Created), activity.Count);
        Assert.All(activity, a => Assert.Equal((PostStatus.Queued, created.Schedule.Id), (a.Status, a.ScheduleId)));
        Assert.All(activity, a => Assert.Contains(shop.Set.Links, l => l.Url == a.TargetUrl));
        Assert.Equal(HttpStatusCode.NotFound, (await shop.Owner.GetAsync($"{Lib(shop.Ws)}/{Guid.NewGuid()}/activity")).StatusCode);
    }

    [Fact]
    public async Task The_selection_bar_acts_on_many_posts_at_once()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws, "เป้าหมาย");
        var p1 = await CreateAsync(client, ws, "หนึ่ง");
        var p2 = await CreateAsync(client, ws, "สอง");
        var p3 = await CreateAsync(client, ws, "สาม", active: false);
        Assert.False(p3.Active);

        async Task<int> Bulk(string action, Guid[] ids, Guid? collection = null) =>
            (await (await client.PostAsJsonAsync($"{Lib(ws)}/bulk", new { postIds = ids, action, collectionId = collection }, Json)).ReadAsync<BulkMasterPostsResultDto>()).Changed;

        Assert.Equal(2, await Bulk("add_to_collection", [p1.Id, p2.Id], c.Id));
        Assert.Equal(0, await Bulk("add_to_collection", [p1.Id], c.Id)); // already in
        Assert.Equal(2, (await client.CollectionsAsync(ws)).Single().Posts.Count);

        Assert.Equal(2, await Bulk("deactivate", [p1.Id, p2.Id, p3.Id]));
        Assert.All(await ListAsync(client, ws), p => Assert.False(p.Active));
        Assert.Equal(3, await Bulk("activate", [p1.Id, p2.Id, p3.Id]));

        Assert.Equal(1, await Bulk("remove_from_collection", [p1.Id, p3.Id], c.Id));
        Assert.Equal([p2.Id], (await client.CollectionsAsync(ws)).Single().Posts.Select(p => p.Id));

        Assert.Equal(2, await Bulk("delete", [p1.Id, p2.Id]));
        Assert.Equal([p3.Id], (await ListAsync(client, ws)).Select(p => p.Id));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Lib(ws)}/bulk", new { postIds = new[] { p3.Id }, action = "add_to_collection" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Lib(ws)}/bulk", new { postIds = Array.Empty<Guid>(), action = "delete" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"{Lib(ws)}/bulk", new { postIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()), action = "delete" }, Json)).StatusCode);
    }

    [Fact]
    public async Task Approval_goes_through_the_library_for_a_collection_that_requires_it()
    {
        var t = await factory.TeamAsync();
        var strict = await t.Owner.CreateCollectionAsync(t.Ws, "ตรวจก่อน");
        (await t.Owner.PutAsJsonAsync($"/api/workspaces/{t.Ws}/collections/{strict.Id}",
            new { name = "ตรวจก่อน", settings = WorkflowTestSupport.Settings(requireApproval: true) }, Json)).EnsureSuccessStatusCode();

        var post = await CreateAsync(t.Editor, t.Ws, "รออนุมัติ", [strict.Id]);
        Assert.Equal(PostApproval.Draft, post.Approval);

        Assert.Equal(PostApproval.Pending,
            (await (await t.Editor.PostAsJsonAsync($"{Lib(t.Ws)}/{post.Id}/approval", new { action = "request" }, Json)).ReadAsync<CollectionPostDto>()).Approval);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Editor.PostAsJsonAsync($"{Lib(t.Ws)}/{post.Id}/approval", new { action = "approve" }, Json)).StatusCode);
        Assert.Equal(PostApproval.Approved,
            (await (await t.Admin.PostAsJsonAsync($"{Lib(t.Ws)}/{post.Id}/approval", new { action = "approve" }, Json)).ReadAsync<CollectionPostDto>()).Approval);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync($"{Lib(t.Ws)}/{post.Id}/approval", new { action = "nope" }, Json)).StatusCode);

        // Editing the text of an approved post that sits in a collection requiring approval sends it back to draft.
        var edited = await (await t.Editor.PutAsJsonAsync($"{Lib(t.Ws)}/{post.Id}", new { text = "แก้ข้อความ" }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal(PostApproval.Draft, edited.Approval);
    }

    [Fact]
    public async Task Viewers_read_the_library_and_cannot_change_it()
    {
        var t = await factory.TeamAsync();
        var post = await CreateAsync(t.Owner, t.Ws);

        Assert.Equal([post.Id], (await ListAsync(t.Viewer, t.Ws)).Select(p => p.Id));
        Assert.Equal(HttpStatusCode.OK, (await t.Viewer.GetAsync($"{Lib(t.Ws)}/{post.Id}/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync(Lib(t.Ws), new { text = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/{post.Id}", new { text = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/{post.Id}/active", new { active = false }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync($"{Lib(t.Ws)}/{post.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t.Viewer.PostAsJsonAsync($"{Lib(t.Ws)}/bulk", new { postIds = new[] { post.Id }, action = "delete" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Editor.PutAsJsonAsync($"{Lib(t.Ws)}/{post.Id}/active", new { active = false }, Json)).StatusCode);

        // Another workspace sees nothing of it.
        var (other, _, otherWs) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Lib(t.Ws))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Lib(otherWs)}/{post.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_workspace_holds_at_most_5000_posts_in_the_library()
    {
        var (client, _, ws) = await factory.SignUpAsync("agency");
        var now = DateTimeOffset.UtcNow;
        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < CollectionPost.MaxPerWorkspace; i++) db.CollectionPosts.Add(CollectionPost.Create(ws, $"โพสต์ {i}", [], false, now));
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Lib(ws), new { text = "โพสต์ที่ 5001" }, Json)).StatusCode);
        var first = (await ListAsync(client, ws))[0];
        (await client.DeleteAsync($"{Lib(ws)}/{first.Id}")).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Lib(ws), new { text = "ตอนนี้ได้แล้ว" }, Json)).EnsureSuccessStatusCode();
        Assert.Equal(CollectionPost.MaxPerWorkspace, await factory.WithDbAsync(db => db.CollectionPosts.CountAsync(p => p.WorkspaceId == ws)));
    }

    [Fact]
    public async Task A_backup_keeps_the_library_with_its_collections_settings_and_switches()
    {
        var (client, _, ws) = await factory.SignUpAsync("agency");
        var a = await client.CreateCollectionAsync(ws, "A");
        var b = await client.CreateCollectionAsync(ws, "B");
        var shared = await CreateAsync(client, ws, "ใช้ร่วมกัน", [a.Id, b.Id], Own(footer: "ท้ายเฉพาะ", maxPerDay: 3, weekdays: [1]));
        var waiting = await CreateAsync(client, ws, "รออยู่", active: false);

        var backup = await client.GetAsync($"/api/workspaces/{ws}/backup");
        backup.EnsureSuccessStatusCode();
        var file = await backup.Content.ReadAsStringAsync();
        (await client.PostAsync($"/api/workspaces/{ws}/restore", new StringContent(file, System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();

        var restored = await ListAsync(client, ws);
        Assert.Equal(["ใช้ร่วมกัน", "รออยู่"], restored.Select(p => p.Text));
        var r1 = restored[0];
        Assert.Equal(2, r1.CollectionIds.Count);
        Assert.Equal(("ท้ายเฉพาะ", 3), (r1.Settings.Footer, r1.Settings.MaxPerDay));
        Assert.Equal([1], r1.Settings.Weekdays);
        Assert.False(restored[1].Active);
        Assert.Empty(restored[1].CollectionIds);
        Assert.NotEqual(shared.Id, r1.Id); // a restore makes new posts
        Assert.NotEqual(waiting.Id, restored[1].Id);
    }
}
