using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// A schedule queues a fortnight ahead, so editing what it posts to has to reach the posts that are already queued.
[Collection(ApiCollection.Name)]
public class ScheduleSyncTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private const int Bangkok = 420;

    private async Task<(Shop Shop, ScheduleCreatedDto Created)> StartAsync()
    {
        var shop = await factory.ShopAsync(links: 2, posts: 2);
        var (slot, _) = SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(2));
        return (shop, await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Order: "rotate", Offset: Bangkok)));
    }

    private static int Queued(IEnumerable<PostDto> posts, Guid link) => posts.Count(p => p.LinkId == link && p.Status == PostStatus.Queued);

    [Fact]
    public async Task Switching_a_link_off_takes_its_queued_posts_out_and_switching_it_on_brings_them_back_at_the_next_claim()
    {
        var (shop, created) = await StartAsync();
        using var _ = shop;
        var (off, kept) = (shop.Link(0).Id, shop.Link(1).Id);
        var before = await shop.PostsAsync(created.Schedule.Id);
        Assert.True(Queued(before, off) > 10);

        await shop.UpdateLinkAsync(0, enabled: false);

        var after = await shop.PostsAsync(created.Schedule.Id);
        Assert.Equal(0, Queued(after, off));
        Assert.Equal(Queued(before, kept), Queued(after, kept)); // the other group is untouched

        await shop.UpdateLinkAsync(0, enabled: true);
        await shop.ClaimAsync(); // every claim tops the schedules up first
        var again = await shop.PostsAsync(created.Schedule.Id);
        Assert.Equal(Queued(before, off), Queued(again, off));
    }

    [Fact]
    public async Task A_link_with_another_code_or_address_posts_the_new_way_from_the_next_claim()
    {
        var (shop, created) = await StartAsync();
        using var _ = shop;
        var link = shop.Link(0);

        var res = await shop.Owner.PutAsJsonAsync($"{shop.Api}/link-sets/{shop.Set.Id}/links/{link.Id}",
            new { name = link.Name, url = link.Url, code = "NEWCODE", dailyMax = link.DailyMax, enabled = true }, Json);
        res.EnsureSuccessStatusCode();
        Assert.DoesNotContain(await shop.PostsAsync(created.Schedule.Id), p => p.LinkId == link.Id && p.Code == "C0" && p.Status == PostStatus.Queued);

        await shop.ClaimAsync();
        var posts = (await shop.PostsAsync(created.Schedule.Id)).Where(p => p.LinkId == link.Id && p.Status == PostStatus.Queued).ToList();
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.Equal("NEWCODE", p.Code));
    }

    [Fact]
    public async Task Deleting_a_link_takes_its_queued_posts_with_it()
    {
        var (shop, created) = await StartAsync();
        using var _ = shop;
        var gone = shop.Link(0).Id;

        (await shop.Owner.DeleteAsync($"{shop.Api}/link-sets/{shop.Set.Id}/links/{gone}")).EnsureSuccessStatusCode();

        var posts = await shop.PostsAsync(created.Schedule.Id);
        Assert.DoesNotContain(posts, p => p.LinkId == gone);
        Assert.Contains(posts, p => p.LinkId == shop.Link(1).Id);
    }

    [Fact]
    public async Task A_link_added_later_is_posted_to_from_the_next_claim()
    {
        var (shop, created) = await StartAsync();
        using var _ = shop;

        var added = await shop.Owner.AddLinkAsync(shop.Ws, shop.Set.Id, "https://www.facebook.com/groups/late-comer", "L1", "มาทีหลัง");
        await shop.ClaimAsync();

        var posts = await shop.PostsAsync(created.Schedule.Id);
        Assert.True(Queued(posts, added.Id) > 10);
        // Nothing was queued twice for the others.
        Assert.Equal(posts.Count, posts.Select(p => (p.LinkId, p.ScheduledAt)).Distinct().Count());
    }

    [Fact]
    public async Task Pausing_or_deleting_a_schedule_drops_what_waits_for_a_browser_even_when_it_is_already_due()
    {
        var (shop, created) = await StartAsync();
        using var _ = shop;
        shop.Wait(TimeSpan.FromHours(3)); // today's posts are due now but no browser has asked yet
        var due = (await shop.PostsAsync(created.Schedule.Id)).Count(p => p.Status == PostStatus.Queued && p.ScheduledAt <= shop.Now);
        Assert.True(due > 0);

        (await shop.SetActiveAsync(created.Schedule.Id, false)).EnsureSuccessStatusCode();

        Assert.DoesNotContain(await shop.PostsAsync(created.Schedule.Id), p => p.Status == PostStatus.Queued);

        (await shop.SetActiveAsync(created.Schedule.Id, true)).EnsureSuccessStatusCode();
        Assert.Contains(await shop.PostsAsync(created.Schedule.Id), p => p.Status == PostStatus.Queued);

        (await shop.Owner.DeleteAsync($"{shop.Api}/schedules/{created.Schedule.Id}")).EnsureSuccessStatusCode();
        Assert.Empty(await shop.PostsAsync(created.Schedule.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await shop.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
    }
}
