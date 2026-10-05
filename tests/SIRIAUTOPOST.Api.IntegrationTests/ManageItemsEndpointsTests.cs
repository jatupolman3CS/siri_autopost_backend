using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Deleting, renaming and switching on/off the items of the lists: library media, snippets, collections, link sets and
// schedules. A collection or link set that is off makes the schedules that use it queue nothing.
[Collection(ApiCollection.Name)]
public class ManageItemsEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private const int Bangkok = 420;

    private static string Lib(Guid ws) => $"/api/workspaces/{ws}";

    private static async Task<List<MediaDto>> MediaAsync(HttpClient c, Guid ws) =>
        (await c.GetFromJsonAsync<List<MediaDto>>($"{Lib(ws)}/media", Json))!;

    private static async Task<List<SnippetDto>> SnippetsAsync(HttpClient c, Guid ws) =>
        (await c.GetFromJsonAsync<List<SnippetDto>>($"{Lib(ws)}/snippets", Json))!;

    private static ScheduleSpec Daily(string slot) => new(Times: [slot], Order: "rotate", Offset: Bangkok);

    // ---- snippets ----

    [Fact]
    public async Task A_snippet_is_edited_switched_off_and_deleted()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var s = (await (await client.PostAsJsonAsync($"{Lib(ws)}/snippets", new { title = "เดิม", text = "ข้อความเดิม" }, Json)).ReadAsync<SnippetDto>());
        Assert.True(s.Active);

        var edited = await (await client.PutAsJsonAsync($"{Lib(ws)}/snippets/{s.Id}", new { title = "  ใหม่  ", text = "ข้อความใหม่" }, Json)).ReadAsync<SnippetDto>();
        Assert.Equal(("ใหม่", "ข้อความใหม่", true), (edited.Title, edited.Text, edited.Active));

        var off = await (await client.PutAsJsonAsync($"{Lib(ws)}/snippets/{s.Id}/active", new { active = false }, Json)).ReadAsync<SnippetDto>();
        Assert.False(off.Active);
        Assert.False((await SnippetsAsync(client, ws)).Single(x => x.Id == s.Id).Active);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PutAsJsonAsync($"{Lib(ws)}/snippets/{s.Id}", new { title = " ", text = "x" }, Json)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Lib(ws)}/snippets/{s.Id}")).StatusCode);
        Assert.DoesNotContain(await SnippetsAsync(client, ws), x => x.Id == s.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Lib(ws)}/snippets/{s.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_snippet_of_another_workspace_cannot_be_touched_and_a_viewer_cannot_change_one()
    {
        var t = await factory.TeamAsync();
        var s = await (await t.Owner.PostAsJsonAsync($"{Lib(t.Ws)}/snippets", new { title = "ก", text = "ข" }, Json)).ReadAsync<SnippetDto>();

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/snippets/{s.Id}", new { title = "x", text = "y" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/snippets/{s.Id}/active", new { active = false }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync($"{Lib(t.Ws)}/snippets/{s.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Editor.DeleteAsync($"{Lib(t.Ws)}/snippets/{s.Id}")).StatusCode);

        var (other, _, otherWs) = await factory.SignUpAsync();
        var mine = await (await other.PostAsJsonAsync($"{Lib(otherWs)}/snippets", new { title = "ก", text = "ข" }, Json)).ReadAsync<SnippetDto>();
        // The id of a snippet in my workspace through somebody else's workspace is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.DeleteAsync($"{Lib(t.Ws)}/snippets/{mine.Id}")).StatusCode);
        Assert.Contains(await SnippetsAsync(other, otherWs), x => x.Id == mine.Id);
    }

    // ---- media ----

    [Fact]
    public async Task A_file_is_renamed_and_switched_off()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var m = await client.UploadImageAsync(ws, "old.jpg");
        Assert.True(m.Active);

        var renamed = await (await client.PutAsJsonAsync($"{Lib(ws)}/media/{m.Id}", new { name = "  ชื่อใหม่  " }, Json)).ReadAsync<MediaDto>();
        Assert.Equal("ชื่อใหม่", renamed.Name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{Lib(ws)}/media/{m.Id}", new { name = " " }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync($"{Lib(ws)}/media/{m.Id}", new { name = new string('x', 201) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Lib(ws)}/media/{Guid.NewGuid()}", new { name = "x" }, Json)).StatusCode);

        var off = await (await client.PostAsJsonAsync($"{Lib(ws)}/media/active", new { mediaIds = new[] { m.Id }, active = false }, Json)).ReadAsync<List<MediaDto>>();
        Assert.False(Assert.Single(off).Active);
        var listed = (await MediaAsync(client, ws)).Single(x => x.Id == m.Id);
        Assert.Equal(("ชื่อใหม่", false), (listed.Name, listed.Active));
    }

    [Fact]
    public async Task Deleting_files_takes_them_off_the_collection_posts_but_keeps_the_text()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var keep = await client.UploadImageAsync(ws, "keep.jpg");
        var gone1 = await client.UploadImageAsync(ws, "a.jpg");
        var gone2 = await client.UploadImageAsync(ws, "b.jpg");
        var c = await client.CreateCollectionAsync(ws);
        await client.AddPostAsync(ws, c.Id, "มีรูป", [keep.Id, gone1.Id]);
        await client.AddPostAsync(ws, c.Id, "มีสองรูป", [gone1.Id, gone2.Id]);

        var res = await client.PostAsJsonAsync($"{Lib(ws)}/media/delete", new { mediaIds = new[] { gone1.Id, gone2.Id, Guid.NewGuid() } }, Json);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal([keep.Id], (await MediaAsync(client, ws)).Select(m => m.Id));
        var posts = (await client.CollectionsAsync(ws)).Single(x => x.Id == c.Id).Posts;
        Assert.Equal("มีรูป", posts[0].Text);
        Assert.Equal([keep.Id], posts[0].MediaIds);
        Assert.Equal("มีสองรูป", posts[1].Text);
        Assert.Empty(posts[1].MediaIds);
        // A deleted file has no content any more.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Lib(ws)}/media/{gone1.Id}/content")).StatusCode);
    }

    [Fact]
    public async Task Files_of_another_workspace_are_not_deleted_and_a_viewer_cannot_delete()
    {
        var t = await factory.TeamAsync();
        var mine = await t.Owner.UploadImageAsync(t.Ws);
        var (other, _, otherWs) = await factory.SignUpAsync();
        var theirs = await other.UploadImageAsync(otherWs);

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync($"{Lib(t.Ws)}/media/delete", new { mediaIds = new[] { mine.Id } }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/media/{mine.Id}", new { name = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t.Viewer.PostAsJsonAsync($"{Lib(t.Ws)}/media/active", new { mediaIds = new[] { mine.Id }, active = false }, Json)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostAsJsonAsync($"{Lib(t.Ws)}/media/delete", new { mediaIds = new[] { theirs.Id } }, Json)).StatusCode);
        Assert.Contains(await MediaAsync(other, otherWs), m => m.Id == theirs.Id);
        Assert.Contains(await MediaAsync(t.Owner, t.Ws), m => m.Id == mine.Id);

        var tooMany = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await t.Owner.PostAsJsonAsync($"{Lib(t.Ws)}/media/delete", new { mediaIds = tooMany }, Json)).StatusCode);
    }

    // ---- collections and link sets ----

    [Fact]
    public async Task A_collection_that_is_off_queues_nothing_and_cannot_be_scheduled_until_it_is_on_again()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        var before = (await shop.PostsAsync(created.Schedule.Id)).Count;
        Assert.True(before > 0);
        Assert.True(shop.Collection.Active);

        var off = await (await shop.Owner.PutAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}/active", new { active = false }, Json)).ReadAsync<CollectionDto>();

        Assert.False(off.Active);
        Assert.Equal(2, off.Posts.Count); // switching off never touches the posts
        Assert.Empty(await shop.PostsAsync(created.Schedule.Id)); // what was queued for the future is gone
        var listed = Assert.Single(await shop.SchedulesAsync());
        Assert.Equal(0, listed.UsablePosts);
        // A new schedule from it is refused with a reason.
        var refused = await shop.TryCreateScheduleAsync(Daily(slot));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var on = await (await shop.Owner.PutAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}/active", new { active = true }, Json)).ReadAsync<CollectionDto>();

        Assert.True(on.Active);
        Assert.Equal(before, (await shop.PostsAsync(created.Schedule.Id)).Count); // the schedule filled its fortnight again
        Assert.Equal(2, Assert.Single(await shop.SchedulesAsync()).UsablePosts);
    }

    [Fact]
    public async Task A_link_set_that_is_off_queues_nothing_until_it_is_on_again()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        var before = (await shop.PostsAsync(created.Schedule.Id)).Count;

        var off = await (await shop.Owner.PutAsJsonAsync($"{shop.Api}/link-sets/{shop.Set.Id}/active", new { active = false }, Json)).ReadAsync<LinkSetDto>();

        Assert.False(off.Active);
        Assert.Equal(2, off.Links.Count);
        Assert.Empty(await shop.PostsAsync(created.Schedule.Id));
        Assert.Equal(0, Assert.Single(await shop.SchedulesAsync()).TargetCount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await shop.TryCreateScheduleAsync(Daily(slot))).StatusCode);
        Assert.Null(await shop.ClaimAsync()); // nothing may be posted to its groups

        await (await shop.Owner.PutAsJsonAsync($"{shop.Api}/link-sets/{shop.Set.Id}/active", new { active = true }, Json)).ReadAsync<LinkSetDto>();

        Assert.Equal(before, (await shop.PostsAsync(created.Schedule.Id)).Count);
    }

    [Fact]
    public async Task Switching_a_collection_or_set_is_an_editors_job()
    {
        var t = await factory.TeamAsync();
        var c = await t.Owner.CreateCollectionAsync(t.Ws);
        var set = await t.Owner.CreateLinkSetAsync(t.Ws);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/collections/{c.Id}/active", new { active = false }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t.Viewer.PutAsJsonAsync($"{Lib(t.Ws)}/link-sets/{set.Id}/active", new { active = false }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await t.Editor.PutAsJsonAsync($"{Lib(t.Ws)}/collections/{c.Id}/active", new { active = false }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await t.Editor.PutAsJsonAsync($"{Lib(t.Ws)}/collections/{Guid.NewGuid()}/active", new { active = false }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_collection_and_a_link_set_are_renamed_through_their_edit_calls()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws, "เดิม");
        var set = await client.CreateLinkSetAsync(ws, "ชุดเดิม");

        var settings = WorkflowTestSupport.Settings();
        var cRes = await client.PutAsJsonAsync($"{Lib(ws)}/collections/{c.Id}", new { name = "ชื่อใหม่", description = "", settings }, Json);
        var sRes = await client.PutAsJsonAsync($"{Lib(ws)}/link-sets/{set.Id}", new { name = "ชุดใหม่", postAsAccountId = (Guid?)null, accountIds = Array.Empty<Guid>() }, Json);

        Assert.Equal("ชื่อใหม่", (await cRes.ReadAsync<CollectionDto>()).Name);
        Assert.Equal("ชุดใหม่", (await sRes.ReadAsync<LinkSetDto>()).Name);
    }

    // ---- schedules ----

    [Fact]
    public async Task A_schedule_is_renamed_without_touching_its_posts()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        var created = await shop.CreateScheduleAsync(Daily(slot));
        var before = (await shop.PostsAsync(created.Schedule.Id)).Select(p => p.Id).ToHashSet();

        var renamed = await (await shop.Owner.PutAsJsonAsync($"{shop.Api}/schedules/{created.Schedule.Id}/name", new { name = "  ตารางเช้า  " }, Json)).ReadAsync<ScheduleDto>();

        Assert.Equal("ตารางเช้า", renamed.Name);
        Assert.Equal(created.Schedule.Times, renamed.Times);
        Assert.True(renamed.Active);
        Assert.Equal(before, (await shop.PostsAsync(created.Schedule.Id)).Select(p => p.Id).ToHashSet());
        Assert.Equal("ตารางเช้า", Assert.Single(await shop.SchedulesAsync()).Name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await shop.Owner.PutAsJsonAsync($"{shop.Api}/schedules/{created.Schedule.Id}/name", new { name = "  " }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await shop.Owner.PutAsJsonAsync($"{shop.Api}/schedules/{created.Schedule.Id}/name", new { name = new string('x', 121) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await shop.Owner.PutAsJsonAsync($"{shop.Api}/schedules/{Guid.NewGuid()}/name", new { name = "x" }, Json)).StatusCode);
    }
}
