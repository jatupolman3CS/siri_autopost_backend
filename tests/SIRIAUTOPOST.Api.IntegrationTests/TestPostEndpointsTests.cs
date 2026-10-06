using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The test page: one real post, due now, composed like a schedule's task.
[Collection(ApiCollection.Name)]
public class TestPostEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static async Task<string> TitleAsync(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title!;

    private static Task<HttpResponseMessage> SendAsync(Shop shop, object body, HttpClient? as_ = null) =>
        (as_ ?? shop.Owner).PostAsJsonAsync($"{shop.Api}/test-post", body, Json);

    [Fact]
    public async Task A_test_post_is_one_real_post_to_the_group_due_now_composed_with_its_code_and_the_footer()
    {
        using var shop = await factory.ShopAsync(links: 2, posts: 1, footer: "สั่งซื้อ 081-234-5678");
        var head = await shop.HeadAsync();

        var res = await SendAsync(shop, new { linkSetId = shop.Set.Id, linkId = shop.Link(1).Id, collectionId = shop.Collection.Id });

        var post = await res.ReadAsync<PostDto>();
        Assert.True(post.IsTest);
        Assert.Equal((PostStatus.Queued, Platform.Fb, shop.Pair.AccountId), (post.Status, post.Platform, post.AccountId));
        Assert.InRange(post.ScheduledAt, shop.Now.AddMinutes(-1), shop.Now.AddMinutes(1));
        Assert.Equal((shop.Link(1).Id, shop.Link(1).Url, "C1", "กลุ่ม 1"), (post.LinkId, post.TargetUrl, post.Code, post.Target));
        Assert.Equal((shop.Posts[0].Id, null), (post.CollectionPostId, post.ScheduleId));
        Assert.Equal("C1\nโพสต์ 1\n\nสั่งซื้อ 081-234-5678", post.Content);

        // The browser takes it like any other, and the web app is told it is there.
        var job = (await shop.ClaimAsync())!;
        Assert.Equal((post.Id, shop.Link(1).Url, post.Content), (job.PostId, job.GroupUrl, job.Content));
        Assert.Equal(PostStatus.Success, (await shop.ReportAsync(post.Id)).Status);
        var events = await shop.EventsAsync("post", head);
        Assert.Contains(events, e => e.Payload.GetProperty("postId").GetGuid() == post.Id && e.Payload.GetProperty("status").GetString() == "queued");
    }

    [Fact]
    public async Task A_chosen_post_and_a_text_of_your_own_are_used_with_the_code_still_applied()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 3);
        var chosen = shop.Posts[2];

        var post = await (await SendAsync(shop, new
        {
            linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id, collectionPostId = chosen.Id,
            text = "  ข้อความทดสอบของฉัน {{code}}  ",
        })).ReadAsync<PostDto>();

        Assert.Equal("ข้อความทดสอบของฉัน C0", post.Content);
        Assert.Equal(chosen.Id, post.CollectionPostId);

        var plain = await (await SendAsync(shop, new
        {
            linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id, collectionPostId = chosen.Id,
        })).ReadAsync<PostDto>();
        Assert.Equal("C0\nโพสต์ 3", plain.Content);
    }

    [Fact]
    public async Task A_post_with_no_choice_is_a_random_usable_one_and_carries_its_media()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 0);
        var image = await shop.Owner.UploadImageAsync(shop.Ws);
        var withMedia = await shop.Owner.AddPostAsync(shop.Ws, shop.Collection.Id, "มีรูป", [image.Id]);

        var post = await (await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id })).ReadAsync<PostDto>();

        Assert.Equal((withMedia.Id, "C0\nมีรูป"), (post.CollectionPostId, post.Content)); // no linkId: the set's first usable link
        Assert.Equal([image.Id], post.MediaIds);
        Assert.Equal(shop.Link(0).Id, post.LinkId);
        Assert.Equal([image.Id], (await shop.ClaimAsync())!.Media.Select(m => m.Id));
    }

    [Fact]
    public async Task Another_account_posts_to_its_default_target_if_it_is_connected()
    {
        using var shop = await factory.ShopAsync(links: 1);

        var post = await (await SendAsync(shop, new { linkSetId = shop.Set.Id, accountId = shop.Pair.AccountId, collectionId = shop.Collection.Id })).ReadAsync<PostDto>();

        Assert.Equal((shop.Pair.AccountId, "กลุ่ม Facebook", null, null, null), (post.AccountId, post.Target, post.LinkId, post.Code, post.TargetUrl));
        Assert.True(post.IsTest);

        // A demo account has no browser: nobody could send it, so it is refused.
        var ig = await factory.SeedAccountAsync(shop.Ws);
        var refused = await SendAsync(shop, new { linkSetId = shop.Set.Id, accountId = ig.Id, collectionId = shop.Collection.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("ยังไม่ได้เชื่อมกับเครื่อง", await TitleAsync(refused));
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_set_has_no_browser_to_post_it()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var collection = await client.CreateCollectionAsync(ws);
        await client.AddPostAsync(ws, collection.Id);
        var set = await client.CreateLinkSetAsync(ws);
        var link = await client.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/plants");

        var res = await client.PostAsJsonAsync($"/api/workspaces/{ws}/test-post", new { linkSetId = set.Id, linkId = link.Id, collectionId = collection.Id }, Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("ผูกเครื่อง", await TitleAsync(res));
    }

    [Fact]
    public async Task A_link_that_is_off_invalid_or_not_in_the_set_is_refused()
    {
        using var shop = await factory.ShopAsync(links: 2);
        await shop.UpdateLinkAsync(1, enabled: false);
        var bad = await shop.Owner.AddLinkAsync(shop.Ws, shop.Set.Id, "https://example.com/nope");
        var otherSet = await shop.Owner.CreateLinkSetAsync(shop.Ws, "อีกชุด");
        var foreign = await shop.Owner.AddLinkAsync(shop.Ws, otherSet.Id, "https://www.facebook.com/groups/elsewhere");

        async Task<HttpStatusCode> Try(Guid link) =>
            (await SendAsync(shop, new { linkSetId = shop.Set.Id, linkId = link, collectionId = shop.Collection.Id })).StatusCode;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(shop.Link(1).Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try(bad.Id));
        Assert.Equal(HttpStatusCode.NotFound, await Try(foreign.Id));
        Assert.Equal(HttpStatusCode.NotFound, await Try(Guid.NewGuid()));
    }

    [Fact]
    public async Task Missing_or_unusable_collections_and_posts_are_refused()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 0);

        // No post at all and no text of your own.
        var empty = await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        // With a text of your own it goes (there is just no collection post behind it).
        var own = await (await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id, text = "ทดสอบ" })).ReadAsync<PostDto>();
        Assert.Equal(("C0\nทดสอบ", null), (own.Content, own.CollectionPostId));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(shop, new { linkSetId = Guid.NewGuid(), collectionId = shop.Collection.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id, collectionPostId = Guid.NewGuid() })).StatusCode);

        // A post that is not approved yet cannot be sent when the collection asks for approval.
        (await shop.Owner.PutAsJsonAsync($"{shop.Api}/collections/{shop.Collection.Id}",
            new { name = "โปรโมชัน", description = "", icon = (string?)null, settings = WorkflowTestSupport.Settings(requireApproval: true) }, Json)).EnsureSuccessStatusCode();
        var draft = await shop.Owner.AddPostAsync(shop.Ws, shop.Collection.Id, "แบบร่าง");
        var notApproved = await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id, collectionPostId = draft.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notApproved.StatusCode);
        Assert.Contains("อนุมัติ", await TitleAsync(notApproved));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id })).StatusCode); // random: nothing usable
    }

    [Fact]
    public async Task A_post_text_fits_with_the_largest_footer_and_a_text_over_5000_is_a_400()
    {
        using var shop = await factory.ShopAsync(links: 1, posts: 1, footer: new string('ข', 1000));

        // A full-size text, the largest footer and the group code still fit what a composed post may hold (7,000).
        var res = await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id, text = new string('ก', 5000) });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var tooLong = await SendAsync(shop, new { linkSetId = shop.Set.Id, collectionId = shop.Collection.Id, text = new string('ก', 5001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Editors_can_test_and_viewers_cannot()
    {
        using var shop = await factory.ShopAsync("agency", links: 1);
        async Task<HttpClient> Join(string role)
        {
            var (client, auth, _) = await factory.SignUpAsync();
            (await shop.Owner.PostAsJsonAsync($"{shop.Api}/members", new { email = auth.User.Email, role }, Json)).EnsureSuccessStatusCode();
            return client;
        }
        var body = new { linkSetId = shop.Set.Id, linkId = shop.Link(0).Id, collectionId = shop.Collection.Id };

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(shop, body, await Join("viewer"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(shop, body, await Join("editor"))).StatusCode);
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(shop, body, stranger)).StatusCode);
    }
}
