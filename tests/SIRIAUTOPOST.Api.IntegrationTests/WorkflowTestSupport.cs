using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Api.IntegrationTests;

/// <summary>Shared helpers of the collection / link set / schedule tests.</summary>
internal static class WorkflowTestSupport
{
    public static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    /// <summary>Runs <paramref name="work"/> with the API's database context (for rows the API cannot create yet).</summary>
    public static async Task<T> WithDbAsync<T>(this ApiFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static Task WithDbAsync(this ApiFactory factory, Func<AppDbContext, Task> work) =>
        factory.WithDbAsync<object?>(async db =>
        {
            await work(db);
            return null;
        });

    public static async Task<MediaDto> UploadImageAsync(this HttpClient client, Guid ws, string name = "poster.jpg")
    {
        var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(bytes, "file", name);
        var res = await client.PostAsync($"/api/workspaces/{ws}/media", form);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MediaDto>(Json))!;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage res)
    {
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<CollectionDto> CreateCollectionAsync(this HttpClient client, Guid ws, string name = "โปรโมชัน", string? description = null) =>
        await (await client.PostAsJsonAsync($"/api/workspaces/{ws}/collections", new { name, description }, Json)).ReadAsync<CollectionDto>();

    /// <summary>A complete settings body: the API wants every field (a missing one is a 400).</summary>
    public static object Settings(
        bool requireApproval = false, string hashtags = "", string footer = "", string footerPos = "end", string pageTags = "", bool shuffle = true,
        bool watermark = false, string watermarkPos = "br") =>
        new { hashtags, pageTags, footer, footerPos, shuffle, watermark, watermarkPos, requireApproval };

    public static async Task<CollectionPostDto> AddPostAsync(
        this HttpClient client, Guid ws, Guid collection, string text = "โพสต์ทดสอบ", IEnumerable<Guid>? media = null) =>
        await (await client.PostAsJsonAsync($"/api/workspaces/{ws}/collections/{collection}/posts", new { text, mediaIds = media ?? [] }, Json))
            .ReadAsync<CollectionPostDto>();

    public static async Task<LinkSetDto> CreateLinkSetAsync(this HttpClient client, Guid ws, string name = "กลุ่มขายของ", Guid? postAs = null) =>
        await (await client.PostAsJsonAsync($"/api/workspaces/{ws}/link-sets", new { name, postAsAccountId = postAs }, Json)).ReadAsync<LinkSetDto>();

    public static async Task<SetLinkDto> AddLinkAsync(
        this HttpClient client, Guid ws, Guid set, string? url, string? code = null, string? name = null, int? dailyMax = null) =>
        await (await client.PostAsJsonAsync($"/api/workspaces/{ws}/link-sets/{set}/links", new { name, url, code, dailyMax }, Json)).ReadAsync<SetLinkDto>();

    public static async Task<List<LinkSetDto>> LinkSetsAsync(this HttpClient client, Guid ws) =>
        (await client.GetFromJsonAsync<List<LinkSetDto>>($"/api/workspaces/{ws}/link-sets", Json))!;

    public static async Task<List<CollectionDto>> CollectionsAsync(this HttpClient client, Guid ws) =>
        (await client.GetFromJsonAsync<List<CollectionDto>>($"/api/workspaces/{ws}/collections", Json))!;

    /// <summary>Signs up an owner on the Agency plan (many seats) with an editor, an admin and a viewer already in the workspace.</summary>
    public static async Task<Team> TeamAsync(this ApiFactory factory)
    {
        var (owner, _, ws) = await factory.SignUpAsync("agency");
        async Task<HttpClient> Join(string role)
        {
            var (client, auth, _) = await factory.SignUpAsync();
            (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/members", new { email = auth.User.Email, role }, Json)).EnsureSuccessStatusCode();
            return client;
        }
        return new Team(owner, await Join("admin"), await Join("editor"), await Join("viewer"), ws);
    }

    public sealed record Team(HttpClient Owner, HttpClient Admin, HttpClient Editor, HttpClient Viewer, Guid Ws);

    /// <summary>Pairs a browser: returns the device client and the Facebook account it created.</summary>
    public static async Task<(HttpClient Device, PairResultDto Pair)> PairDeviceAsync(this ApiFactory factory, HttpClient owner, Guid ws)
    {
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content.ReadFromJsonAsync<PairingCodeDto>(Json))!;
        var device = factory.CreateClient();
        var pair = (await (await device.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "Shop PC", browser = "Chrome", version = "2.2.0" }, Json))
            .Content.ReadFromJsonAsync<PairResultDto>(Json))!;
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        return (device, pair);
    }

    public static async Task<AccountDto> AccountOfAsync(this HttpClient client, Guid ws, Platform platform, bool connected = false) =>
        (await client.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!
            .First(a => a.Platform == platform && a.Connected == connected);

    public const string FillerTarget = "ตัวเต็ม";

    /// <summary>
    /// Makes the workspace's queue hold <paramref name="count"/> more posts that are still to go out (a month ahead, so no
    /// claim touches them), straight in the database: the API would need hours of schedules to get there.
    /// </summary>
    public static async Task FillQueueAsync(this ApiFactory factory, Guid ws, Guid accountId, int count)
    {
        var now = DateTimeOffset.UtcNow;
        for (var done = 0; done < count; done += 2000)
        {
            await factory.WithDbAsync(async db =>
            {
                var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
                db.Posts.AddRange(Enumerable.Range(0, Math.Min(2000, count - done))
                    .Select(i => Post.Schedule(ws, account, FillerTarget, "ข้อความ", [], now.AddDays(30).AddSeconds(i), now)));
                await db.SaveChangesAsync();
            });
        }
    }

    /// <summary>Takes the posts <see cref="FillQueueAsync"/> made out of the queue again.</summary>
    public static Task EmptyFillerAsync(this ApiFactory factory, Guid ws) =>
        factory.WithDbAsync(db => db.Posts.Where(p => p.WorkspaceId == ws && p.Target == FillerTarget).ExecuteDeleteAsync());

    /// <summary>Queued posts of the workspace that are still in the future at <paramref name="now"/> (the API's clock): what the queue limit counts.</summary>
    public static Task<int> QueuedAsync(this ApiFactory factory, Guid ws, DateTimeOffset now) =>
        factory.WithDbAsync(db => db.Posts.CountAsync(p => p.WorkspaceId == ws && p.Status == PostStatus.Queued && p.ScheduledAt > now));
}
