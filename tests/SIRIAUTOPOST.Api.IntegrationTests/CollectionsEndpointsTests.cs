using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class CollectionsEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;

    private static string Base(Guid ws) => $"/api/workspaces/{ws}/collections";

    [Fact]
    public async Task A_new_workspace_has_no_collections_and_a_collection_starts_empty()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        Assert.Empty(await client.CollectionsAsync(ws)); // nothing is seeded: a demo schedule would post to real groups

        var c = await client.CreateCollectionAsync(ws, " โปรโมชัน ", "ของลดราคา");

        Assert.Equal("โปรโมชัน", c.Name);
        Assert.Equal("ของลดราคา", c.Description);
        Assert.Equal("ph-folder", c.Icon);
        Assert.Empty(c.Posts);
        Assert.Equal(0, c.ScheduleCount);
        Assert.Equal(new CollectionSettingsDto("", "", "", FooterPosition.End, true, false, WatermarkPosition.Br, false), c.Settings);
        Assert.Single(await client.CollectionsAsync(ws));
    }

    [Fact]
    public async Task Collections_are_edited_with_their_composing_settings()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws);
        var first = await client.CreateCollectionAsync(ws, "ชุดที่สอง");

        var settings = new
        {
            hashtags = "#บ้าน #สวน", pageTags = "เพจ A | https://facebook.com/a", footer = "ทักแชทได้เลย", footerPos = "top", shuffle = false,
            watermark = true, watermarkPos = "tr", requireApproval = true,
        };
        var res = await client.PutAsJsonAsync($"{Base(ws)}/{c.Id}", new { name = "ชื่อใหม่", description = "d", icon = "ph-star", settings }, Json);
        var updated = await res.ReadAsync<CollectionDto>();

        Assert.Equal(("ชื่อใหม่", "d", "ph-star"), (updated.Name, updated.Description, updated.Icon));
        Assert.Equal(new CollectionSettingsDto("#บ้าน #สวน", "เพจ A | https://facebook.com/a", "ทักแชทได้เลย", FooterPosition.Top, false, true, WatermarkPosition.Tr, true),
            updated.Settings);
        var all = await client.CollectionsAsync(ws);
        Assert.Equal([c.Id, first.Id], all.Select(x => x.Id)); // creation order is kept

        // No icon keeps the old one.
        var keep = await (await client.PutAsJsonAsync($"{Base(ws)}/{c.Id}", new { name = "ชื่อใหม่", settings }, Json)).ReadAsync<CollectionDto>();
        Assert.Equal("ph-star", keep.Icon);
    }

    [Fact]
    public async Task Posts_are_added_edited_and_deleted()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws);
        var media = await client.UploadImageAsync(ws);

        var post = await client.AddPostAsync(ws, c.Id, "  ขายของ {ดี|เยี่ยม} {{code}}  ", [media.Id]);
        Assert.Equal("ขายของ {ดี|เยี่ยม} {{code}}", post.Text);
        Assert.Equal([media.Id], post.MediaIds);
        Assert.Equal((PostApproval.Approved, 0, true), (post.Approval, post.PostedCount, post.Active));
        Assert.Equal([c.Id], post.CollectionIds);

        var edited = await (await client.PutAsJsonAsync($"{Base(ws)}/{c.Id}/posts/{post.Id}", new { text = "แก้แล้ว", mediaIds = Array.Empty<Guid>() }, Json))
            .ReadAsync<CollectionPostDto>();
        Assert.Equal(("แก้แล้ว", 0), (edited.Text, edited.MediaIds.Count));
        Assert.True(edited.UpdatedAt >= edited.CreatedAt);

        var second = await client.AddPostAsync(ws, c.Id, "โพสต์ที่สอง");
        var listed = (await client.CollectionsAsync(ws)).Single().Posts;
        Assert.Equal([post.Id, second.Id], listed.Select(p => p.Id)); // newest last

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base(ws)}/{c.Id}/posts/{post.Id}")).StatusCode);
        Assert.Equal([second.Id], (await client.CollectionsAsync(ws)).Single().Posts.Select(p => p.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Base(ws)}/{c.Id}/posts/{post.Id}")).StatusCode);

        // Deleting the collection (or taking a post out of it) leaves the posts in the library.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base(ws)}/{c.Id}")).StatusCode);
        Assert.Empty(await client.CollectionsAsync(ws));
        Assert.Equal(2, await factory.WithDbAsync(db => db.CollectionPosts.CountAsync(p => p.WorkspaceId == ws)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.CollectionMembers.CountAsync(m => m.WorkspaceId == ws)));
    }

    [Fact]
    public async Task The_AI_writer_adds_up_to_twenty_posts_at_once_or_none()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws);
        var media = await client.UploadImageAsync(ws);

        var added = await (await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts/batch", new
        {
            items = new object[] { new { text = "หนึ่ง" }, new { text = "สอง", mediaIds = new[] { media.Id } }, new { text = "สาม", mediaIds = Array.Empty<Guid>() } },
        }, Json)).ReadAsync<List<CollectionPostDto>>();
        Assert.Equal(["หนึ่ง", "สอง", "สาม"], added.Select(p => p.Text));
        Assert.Equal([media.Id], added[1].MediaIds);
        Assert.Equal(3, (await client.CollectionsAsync(ws)).Single().Posts.Count);

        var tooMany = await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts/batch",
            new { items = Enumerable.Range(0, 21).Select(i => new { text = $"โพสต์ {i}" }) }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var oneBad = await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts/batch",
            new { items = new object[] { new { text = "ดี" }, new { text = new string('ก', 5001) } } }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, oneBad.StatusCode);
        var unknownMedia = await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts/batch",
            new { items = new object[] { new { text = "ดี" }, new { text = "ไม่ดี", mediaIds = new[] { Guid.NewGuid() } } } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownMedia.StatusCode);
        Assert.Equal(3, (await client.CollectionsAsync(ws)).Single().Posts.Count); // none of them were added

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts/batch", new { items = Array.Empty<object>() }, Json)).StatusCode);
    }

    [Fact]
    public async Task Bad_input_is_reported_field_by_field()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws);

        var noName = await client.PostAsJsonAsync(Base(ws), new { name = " " }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("name", (await noName.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Base(ws), new { name = new string('x', 121) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Base(ws), new { name = "x", description = new string('x', 301) }, Json)).StatusCode);

        var noText = await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts", new { text = "" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, noText.StatusCode);
        Assert.Contains("text", (await noText.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts", new { text = new string('x', 5001) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts", new { text = "x", mediaIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync($"{Base(ws)}/{c.Id}/posts", new { text = "x", mediaIds = new[] { Guid.NewGuid() } }, Json)).StatusCode);

        var settings = new { hashtags = new string('#', 501), pageTags = "", footer = "", footerPos = "end", shuffle = true, watermark = false, watermarkPos = "br", requireApproval = false };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Base(ws)}/{c.Id}", new { name = "x", settings }, Json)).StatusCode);
        var unknownEnum = await client.PutAsJsonAsync($"{Base(ws)}/{c.Id}", new { name = "x", settings = new { hashtags = "", pageTags = "", footer = "", footerPos = "middle", shuffle = true, watermark = false, watermarkPos = "br", requireApproval = false } }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, unknownEnum.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"{Base(ws)}/{Guid.NewGuid()}", new { name = "x", settings = WorkflowTestSupport.Settings() }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Base(ws)}/{Guid.NewGuid()}/posts", new { text = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Base(ws)}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_post_is_only_reachable_through_its_own_collection()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var a = await client.CreateCollectionAsync(ws, "A");
        var b = await client.CreateCollectionAsync(ws, "B");
        var post = await client.AddPostAsync(ws, a.Id);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Base(ws)}/{b.Id}/posts/{post.Id}", new { text = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Base(ws)}/{b.Id}/posts/{post.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Base(ws)}/{b.Id}/posts/{post.Id}/approval", new { action = "request" }, Json)).StatusCode);

        // Another workspace's collection is not reachable either.
        var (other, _, otherWs) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Base(ws))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Base(otherWs)}/{a.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_post_moves_to_another_collection()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var a = await client.CreateCollectionAsync(ws, "A");
        var b = await client.CreateCollectionAsync(ws, "B");
        var strict = await client.CreateCollectionAsync(ws, "ตรวจก่อน");
        await client.PutAsJsonAsync($"{Base(ws)}/{strict.Id}", new { name = "ตรวจก่อน", settings = WorkflowTestSupport.Settings(requireApproval: true) }, Json);
        var post = await client.AddPostAsync(ws, a.Id, "ย้ายฉัน");

        var moved = await (await client.PutAsJsonAsync($"{Base(ws)}/{a.Id}/posts/{post.Id}", new { text = "ย้ายฉัน", collectionId = b.Id }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal([b.Id], moved.CollectionIds);
        Assert.Equal(PostApproval.Approved, moved.Approval);
        var all = await client.CollectionsAsync(ws);
        Assert.Empty(all.Single(x => x.Id == a.Id).Posts);
        Assert.Equal([post.Id], all.Single(x => x.Id == b.Id).Posts.Select(p => p.Id));

        // Into a collection that requires approval: a post somebody approved stays approved, and a change of its text sends it back to draft.
        var into = await (await client.PutAsJsonAsync($"{Base(ws)}/{b.Id}/posts/{post.Id}", new { text = "ย้ายฉัน", collectionId = strict.Id }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal([strict.Id], into.CollectionIds);
        Assert.Equal(PostApproval.Approved, into.Approval);
        var changed = await (await client.PutAsJsonAsync($"{Base(ws)}/{strict.Id}/posts/{post.Id}", new { text = "ย้ายฉันแล้ว" }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal(PostApproval.Draft, changed.Approval);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Base(ws)}/{strict.Id}/posts/{post.Id}", new { text = "x", collectionId = Guid.NewGuid() }, Json)).StatusCode);
    }

    [Fact]
    public async Task Approval_follows_the_roles_of_the_team()
    {
        var t = await factory.TeamAsync();
        var c = await t.Editor.CreateCollectionAsync(t.Ws, "ต้องอนุมัติ");
        (await t.Admin.PutAsJsonAsync($"{Base(t.Ws)}/{c.Id}", new { name = "ต้องอนุมัติ", settings = WorkflowTestSupport.Settings(requireApproval: true) }, Json)).EnsureSuccessStatusCode();

        var post = await t.Editor.AddPostAsync(t.Ws, c.Id, "รออนุมัติ");
        Assert.Equal(PostApproval.Draft, post.Approval);
        string Approval(Guid p) => $"{Base(t.Ws)}/{c.Id}/posts/{p}/approval";

        // The viewer cannot ask, the editor can.
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync(Approval(post.Id), new { action = "request" }, Json)).StatusCode);
        var pending = await (await t.Editor.PostAsJsonAsync(Approval(post.Id), new { action = "request" }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal(PostApproval.Pending, pending.Approval);

        // Only an admin approves or rejects.
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Editor.PostAsJsonAsync(Approval(post.Id), new { action = "approve" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Editor.PostAsJsonAsync(Approval(post.Id), new { action = "reject" }, Json)).StatusCode);
        var rejected = await (await t.Admin.PostAsJsonAsync(Approval(post.Id), new { action = "reject" }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal(PostApproval.Draft, rejected.Approval);

        await t.Editor.PostAsJsonAsync(Approval(post.Id), new { action = "request" }, Json);
        var approved = await (await t.Owner.PostAsJsonAsync(Approval(post.Id), new { action = "approve" }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal(PostApproval.Approved, approved.Approval);

        // The steps cannot be skipped or repeated.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await t.Editor.PostAsJsonAsync(Approval(post.Id), new { action = "request" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await t.Admin.PostAsJsonAsync(Approval(post.Id), new { action = "approve" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.Admin.PostAsJsonAsync(Approval(post.Id), new { action = "publish" }, Json)).StatusCode);

        // An edit needs a new approval.
        var edited = await (await t.Editor.PutAsJsonAsync($"{Base(t.Ws)}/{c.Id}/posts/{post.Id}", new { text = "แก้ข้อความ" }, Json)).ReadAsync<CollectionPostDto>();
        Assert.Equal(PostApproval.Draft, edited.Approval);
    }

    [Fact]
    public async Task Only_an_admin_can_switch_approval_on_or_off_so_an_editor_cannot_get_around_it()
    {
        var t = await factory.TeamAsync();
        var c = await t.Owner.CreateCollectionAsync(t.Ws, "ต้องอนุมัติ");
        Task<HttpResponseMessage> Put(HttpClient who, bool approval, string hashtags = "") =>
            who.PutAsJsonAsync($"{Base(t.Ws)}/{c.Id}", new { name = "ต้องอนุมัติ", settings = WorkflowTestSupport.Settings(requireApproval: approval, hashtags: hashtags) }, Json);
        async Task<CollectionDto> Stored() => (await t.Owner.CollectionsAsync(t.Ws)).Single(x => x.Id == c.Id);

        // The editor cannot turn it on...
        var refused = await Put(t.Editor, true);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("admin", (await refused.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title);
        Assert.False((await Stored()).Settings.RequireApproval);

        // ...an admin can, and so can the owner.
        Assert.True((await (await Put(t.Admin, true)).ReadAsync<CollectionDto>()).Settings.RequireApproval);
        Assert.False((await (await Put(t.Owner, false)).ReadAsync<CollectionDto>()).Settings.RequireApproval);
        (await Put(t.Admin, true)).EnsureSuccessStatusCode();

        // The editor cannot turn it off either: the approval step would be theirs to skip.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(t.Editor, false)).StatusCode);
        Assert.True((await Stored()).Settings.RequireApproval);

        // Everything else stays an editor's: a save that keeps the switch where it is goes through, either way.
        var tagged = await (await Put(t.Editor, true, "#ลดราคา")).ReadAsync<CollectionDto>();
        Assert.Equal(("#ลดราคา", true), (tagged.Settings.Hashtags, tagged.Settings.RequireApproval));
        (await Put(t.Owner, false)).EnsureSuccessStatusCode();
        Assert.Equal("#อีกที", (await (await Put(t.Editor, false, "#อีกที")).ReadAsync<CollectionDto>()).Settings.Hashtags);

        // A viewer still cannot save anything, and an editor's refusal says nothing about collections that are not theirs to see.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(t.Viewer, false)).StatusCode);
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"{Base(t.Ws)}/{c.Id}", new { name = "x", settings = WorkflowTestSupport.Settings(requireApproval: true) }, Json)).StatusCode);
    }

    [Fact]
    public async Task Viewers_read_editors_write()
    {
        var t = await factory.TeamAsync();
        var c = await t.Owner.CreateCollectionAsync(t.Ws);
        var post = await t.Owner.AddPostAsync(t.Ws, c.Id);

        Assert.Single(await t.Viewer.CollectionsAsync(t.Ws));
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync(Base(t.Ws), new { name = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Base(t.Ws)}/{c.Id}", new { name = "x", settings = WorkflowTestSupport.Settings() }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync($"{Base(t.Ws)}/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync($"{Base(t.Ws)}/{c.Id}/posts", new { text = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync($"{Base(t.Ws)}/{c.Id}/posts/batch", new { items = new[] { new { text = "x" } } }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Base(t.Ws)}/{c.Id}/posts/{post.Id}", new { text = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync($"{Base(t.Ws)}/{c.Id}/posts/{post.Id}")).StatusCode);

        var byEditor = await t.Editor.CreateCollectionAsync(t.Ws, "จากบรรณาธิการ");
        await t.Editor.AddPostAsync(t.Ws, byEditor.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Editor.DeleteAsync($"{Base(t.Ws)}/{c.Id}/posts/{post.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Editor.DeleteAsync($"{Base(t.Ws)}/{c.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_collection_a_schedule_uses_cannot_be_deleted()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var c = await client.CreateCollectionAsync(ws, "ใช้อยู่");
        var set = await client.CreateLinkSetAsync(ws);
        await factory.WithDbAsync(async db =>
        {
            db.Schedules.Add(Schedule.Create(ws, "ตารางเช้า", c.Id, set.Id, ScheduleMode.Daily, ["09:00"], 6, "09:00", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "14:00",
                PostOrder.Shuffle, "09:00", "21:00", 3, 0, 0, null, 420, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });

        var refused = await client.DeleteAsync($"{Base(ws)}/{c.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("ตารางเช้า", (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal(1, (await client.CollectionsAsync(ws)).Single().ScheduleCount);
        Assert.Equal(1, (await client.LinkSetsAsync(ws)).Single().ScheduleCount);

        await factory.WithDbAsync(async db =>
        {
            db.Schedules.RemoveRange(db.Schedules.Where(s => s.WorkspaceId == ws));
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base(ws)}/{c.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_workspace_holds_at_most_100_collections_and_5000_posts()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var now = DateTimeOffset.UtcNow;
        PostCollection? last = null;
        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < PostCollection.MaxPerWorkspace; i++)
            {
                last = PostCollection.Create(ws, $"ชุด {i}", null, now, i);
                db.Collections.Add(last);
            }
            await db.SaveChangesAsync();
            for (var i = 0; i < CollectionPost.MaxPerWorkspace; i++)
            {
                var post = CollectionPost.Create(last!, $"โพสต์ {i}", [], now);
                db.CollectionPosts.Add(post);
                db.CollectionMembers.Add(CollectionMember.Create(ws, last!.Id, post.Id, now));
            }
            await db.SaveChangesAsync();
        });

        var more = await client.PostAsJsonAsync(Base(ws), new { name = "ชุดที่ 101" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, more.StatusCode);
        var full = await client.PostAsJsonAsync($"{Base(ws)}/{last!.Id}/posts", new { text = "โพสต์ที่ 5001" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, full.StatusCode);
        var batch = await client.PostAsJsonAsync($"{Base(ws)}/{last.Id}/posts/batch", new { items = new[] { new { text = "x" } } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, batch.StatusCode);

        // Freeing a slot lets the next one in.
        var posts = (await client.CollectionsAsync(ws)).Single(c => c.Id == last.Id).Posts;
        Assert.Equal(CollectionPost.MaxPerWorkspace, posts.Count);
        // (Taking it out of the collection frees nothing: it is still in the library. Deleting it does.)
        (await client.DeleteAsync($"{Base(ws)}/{last.Id}/posts/{posts[0].Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync($"{Base(ws)}/{last.Id}/posts", new { text = "ยังเต็มอยู่" }, Json)).StatusCode);
        (await client.DeleteAsync($"/api/workspaces/{ws}/master-posts/{posts[0].Id}")).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"{Base(ws)}/{last.Id}/posts", new { text = "ตอนนี้ได้แล้ว" }, Json)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Posted_counts_only_successful_posts_that_are_not_tests()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var c = await client.CreateCollectionAsync(ws);
        var used = await client.AddPostAsync(ws, c.Id, "ใช้แล้ว");
        var unused = await client.AddPostAsync(ws, c.Id, "ยังไม่ได้ใช้");
        var now = DateTimeOffset.UtcNow;
        await factory.WithDbAsync(async db =>
        {
            var account = await db.Accounts.FirstAsync(a => a.Id == pair.AccountId);
            Post Make(bool test, bool success)
            {
                var p = test
                    ? Post.Test(ws, account, "g", "t", [], now, used.Id, null, null, null)
                    : Post.FromSchedule(ws, account, "g", "t", [], now.AddHours(1), now, Guid.NewGuid(), used.Id, Guid.NewGuid(), "k" + Guid.NewGuid(), "s", null, null);
                p.Claim(pair.DeviceId, now);
                if (success) p.CompletePosted(false, now);
                else p.Fail(FailureCode.Network, "x", now);
                return p;
            }
            // Two real successes count; a failure and a successful test post do not.
            db.Posts.AddRange(Make(false, true), Make(false, true), Make(false, false), Make(true, true));
            await db.SaveChangesAsync();
        });

        var listed = (await client.CollectionsAsync(ws)).Single().Posts;
        Assert.Equal(2, listed.Single(p => p.Id == used.Id).PostedCount);
        Assert.Equal(0, listed.Single(p => p.Id == unused.Id).PostedCount);
    }
}
