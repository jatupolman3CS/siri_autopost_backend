using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class WorkspaceEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static string Range()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var to = DateTimeOffset.UtcNow.AddDays(30).ToString("O");
        return $"from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
    }

    [Fact]
    public async Task New_workspaces_start_empty_with_no_sample_accounts_or_snippets()
    {
        var (client, _, ws) = await factory.SignUpAsync();

        // Facebook groups and pages are all the system posts to: no sample Instagram, X, TikTok, LINE or Threads accounts.
        var accounts = (await client.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        Assert.Empty(accounts);

        var snippets = (await client.GetFromJsonAsync<List<SnippetDto>>($"/api/workspaces/{ws}/snippets", Json))!;
        Assert.Empty(snippets);
    }

    [Fact]
    public async Task New_workspaces_start_with_no_posts_and_no_error_reports()
    {
        var (client, _, ws) = await factory.SignUpAsync();

        // The calendar and the error list only show what schedules and the extension made: nothing is made up.
        var posts = (await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/posts?{Range()}", Json))!;
        Assert.Empty(posts);
        var errors = (await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/errors", Json))!;
        Assert.Empty(errors);

        var list = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!;
        Assert.Equal(0, list.Single().Posts7);
    }

    [Fact]
    public async Task Schedule_list_and_delete_posts()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var page = await factory.SeedAccountAsync(ws, "เพจตัวอย่าง", "เพจ", groups: ["กลุ่ม 1", "กลุ่ม 2", "กลุ่ม 3"]);
        var ig = await factory.SeedAccountAsync(ws);

        var res = await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "โปรวันนี้",
            startAt = DateTimeOffset.UtcNow.AddHours(2),
            useDelay = true,
            repeat = "none",
            targets = new object[] { new { accountId = page.Id, groups = page.Groups.Take(2) }, new { accountId = ig.Id } },
        }, Json);
        res.EnsureSuccessStatusCode();
        Assert.Equal(3, (await res.Content.ReadFromJsonAsync<ScheduleResultDto>(Json))!.Created);

        async Task<List<PostDto>> Ours() =>
            (await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/posts?{Range()}", Json))!
                .Where(p => p.Content == "โปรวันนี้").ToList();
        var posts = await Ours();
        Assert.Equal(3, posts.Count);
        Assert.All(posts, p => Assert.Equal(PostStatus.Queued, p.Status));

        var del = await client.DeleteAsync($"/api/workspaces/{ws}/posts/{posts[0].Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(2, (await Ours()).Count);
    }

    [Fact]
    public async Task Scheduling_reports_field_and_rule_errors()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var other = await factory.SeedAccountAsync(ws);

        var empty = await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule",
            new { content = "", startAt = DateTimeOffset.UtcNow.AddHours(1), repeat = "none", targets = Array.Empty<object>() }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var problem = await empty.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("content", problem!.Errors.Keys);
        Assert.Contains("targets", problem.Errors.Keys);

        var tiktok = await factory.SeedAccountAsync(ws, "@shop_tt", "โปรไฟล์", AccountHealth.Relogin);
        var relogin = await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule",
            new { content = "hi", startAt = DateTimeOffset.UtcNow.AddHours(1), repeat = "none", targets = new[] { new { accountId = tiktok.Id } } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, relogin.StatusCode);

        var past = await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule",
            new { content = "hi", startAt = DateTimeOffset.UtcNow.AddHours(-1), repeat = "none", targets = new[] { new { accountId = other.Id } } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
    }

    [Fact]
    public async Task Other_users_cannot_see_a_workspace()
    {
        var (_, _, ws) = await factory.SignUpAsync();
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/workspaces/{ws}/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/workspaces/{ws}/engine")).StatusCode);
    }

    [Fact]
    public async Task Engine_settings_respect_the_plan()
    {
        var (client, _, ws) = await factory.SignUpAsync("free");
        var s = (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!;
        var changed = s.AntiBan with { Min = 5, Max = 20, Typing = false };

        var res = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", changed, Json);
        var saved = (await res.Content.ReadFromJsonAsync<EngineSettingsDto>(Json))!;
        Assert.Equal(5, saved.AntiBan.Min);
        Assert.True(saved.AntiBan.Typing); // Pro feature: unchanged on the free plan

        var bad = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", changed with { Min = 30, Max = 10 }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var off = await client.PutAsJsonAsync($"/api/workspaces/{ws}/engine/offline", s.Offline with { Policy = OfflinePolicy.Skip, Window = "day" }, Json);
        Assert.Equal(OfflinePolicy.Skip, (await off.Content.ReadFromJsonAsync<EngineSettingsDto>(Json))!.Offline.Policy);
    }

    [Fact]
    public async Task Offline_simulation_holds_and_releases_due_posts()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var page = await factory.SeedAccountAsync(ws, "เพจตัวอย่าง", "เพจ", groups: ["ก", "ข", "ค", "ง", "จ", "ฉ"]);
        await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "hi",
            startAt = DateTimeOffset.UtcNow.AddHours(1),
            useDelay = true,
            repeat = "none",
            targets = new[] { new { accountId = page.Id, groups = page.Groups.Take(6) } },
        }, Json);

        async Task<int> Count(PostStatus status) =>
            (await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/posts?{Range()}", Json))!
                .Count(p => p.Status == status);
        var sentBefore = await Count(PostStatus.Success);

        var off = await client.PostAsJsonAsync($"/api/workspaces/{ws}/engine/extension", new { online = false });
        Assert.Equal(4, (await off.Content.ReadFromJsonAsync<ExtensionStateDto>(Json))!.Affected);
        Assert.Equal(4, await Count(PostStatus.Waiting));

        var on = await client.PostAsJsonAsync($"/api/workspaces/{ws}/engine/extension", new { online = true });
        Assert.Equal(4, (await on.Content.ReadFromJsonAsync<ExtensionStateDto>(Json))!.Affected);
        Assert.Equal(0, await Count(PostStatus.Waiting));
        Assert.Equal(sentBefore, await Count(PostStatus.Success)); // default policy: back in the queue for a device to send
    }

    [Fact]
    public async Task Upload_media_then_attach_it_to_a_post()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "file", "poster.jpg");

        var up = await client.PostAsync($"/api/workspaces/{ws}/media", form);
        up.EnsureSuccessStatusCode();
        var media = (await up.Content.ReadFromJsonAsync<MediaDto>(Json))!;
        Assert.Equal("poster", media.Name);
        Assert.Equal(MediaKind.Image, media.Kind);

        var content = await client.GetByteArrayAsync($"/api/workspaces/{ws}/media/{media.Id}/content");
        Assert.Equal(7, content.Length);

        var other = await factory.SeedAccountAsync(ws);
        var res = await client.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "มีรูป",
            mediaIds = new[] { media.Id },
            startAt = DateTimeOffset.UtcNow.AddHours(1),
            repeat = "none",
            targets = new[] { new { accountId = other.Id } },
        }, Json);
        res.EnsureSuccessStatusCode();

        var pdf = new MultipartFormDataContent();
        var doc = new ByteArrayContent([1]);
        doc.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        pdf.Add(doc, "file", "a.pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/api/workspaces/{ws}/media", pdf)).StatusCode);
    }

    [Fact]
    public async Task Create_workspace_and_snippet()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var created = await client.PostAsJsonAsync("/api/workspaces", new { name = "Smile+ Clinic" });
        Assert.Equal("Smile+ Clinic", (await created.Content.ReadFromJsonAsync<WorkspaceDto>(Json))!.Name);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Count);

        var snip = await client.PostAsJsonAsync($"/api/workspaces/{ws}/snippets", new { title = "ปิดท้าย", text = "ทักแชทเลย" });
        Assert.Equal("ปิดท้าย", (await snip.Content.ReadFromJsonAsync<SnippetDto>(Json))!.Title);
    }
}
