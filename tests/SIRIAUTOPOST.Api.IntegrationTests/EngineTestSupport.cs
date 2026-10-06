using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

/// <summary>What a schedule is asked to be; the defaults make a daily schedule.</summary>
internal sealed record ScheduleSpec(
    string Mode = "daily", string[]? Times = null, string Order = "rotate", string? StartDate = null, string OnceTime = "14:00",
    int EveryHours = 6, string FirstTime = "09:00", string DripFrom = "09:00", string DripTo = "21:00", int DripCount = 3,
    Dictionary<string, string[]>? Overrides = null, int Offset = 0, string? Name = null, int BumpHours = 0, int AutoDeleteDays = 0, bool StartNow = false,
    object? Bump = null);

/// <summary>
/// A workspace the engine can run: an owner (Pro unless said otherwise) with a browser paired, a collection of posts and
/// a link set of groups. The clock is the API's <see cref="TestClock"/>: <see cref="Wait"/> moves it and dispose puts it
/// back.
/// </summary>
internal sealed class Shop : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private readonly List<IDisposable> _skips = [];

    public required TestClock Clock { get; init; }
    public required HttpClient Owner { get; init; }
    public required Guid Ws { get; init; }
    public required HttpClient Device { get; init; }
    public required PairResultDto Pair { get; init; }
    public required CollectionDto Collection { get; set; }
    public required LinkSetDto Set { get; set; }
    public required AuthResultDto Auth { get; init; }

    public string Api => $"/api/workspaces/{Ws}";
    public DateTimeOffset Now => Clock.GetUtcNow();
    public SetLinkDto Link(int i) => Set.Links[i];
    public IReadOnlyList<CollectionPostDto> Posts => Collection.Posts;

    public void Wait(TimeSpan by) => _skips.Add(Clock.Advance(by));

    public void WaitUntil(DateTimeOffset at) => Wait(at - Now + TimeSpan.FromMinutes(1));

    public void Dispose()
    {
        for (var i = _skips.Count - 1; i >= 0; i--) _skips[i].Dispose();
        _skips.Clear();
    }

    // ---- schedules ----

    public object Body(ScheduleSpec spec, Guid? collection = null, Guid? set = null) => new
    {
        name = spec.Name,
        collectionId = collection ?? Collection.Id,
        linkSetId = set ?? Set.Id,
        mode = spec.Mode,
        times = spec.Times ?? ["18:00"],
        everyHours = spec.EveryHours,
        firstTime = spec.FirstTime,
        startDate = spec.StartDate,
        onceTime = spec.OnceTime,
        order = spec.Order,
        dripFrom = spec.DripFrom,
        dripTo = spec.DripTo,
        dripCount = spec.DripCount,
        bumpHours = spec.BumpHours,
        autoDeleteDays = spec.AutoDeleteDays,
        overrides = spec.Overrides,
        utcOffsetMinutes = spec.Offset,
        startNow = spec.StartNow,
        bump = spec.Bump,
    };

    public Task<HttpResponseMessage> TryCreateScheduleAsync(ScheduleSpec? spec = null, HttpClient? as_ = null) =>
        (as_ ?? Owner).PostAsJsonAsync($"{Api}/schedules", Body(spec ?? new ScheduleSpec()), Json);

    public async Task<ScheduleCreatedDto> CreateScheduleAsync(ScheduleSpec? spec = null) =>
        await (await TryCreateScheduleAsync(spec)).ReadAsync<ScheduleCreatedDto>();

    public async Task<List<ScheduleDto>> SchedulesAsync(HttpClient? as_ = null) =>
        (await (as_ ?? Owner).GetFromJsonAsync<List<ScheduleDto>>($"{Api}/schedules", Json))!;

    public Task<HttpResponseMessage> SetActiveAsync(Guid schedule, bool active, HttpClient? as_ = null) =>
        (as_ ?? Owner).PutAsJsonAsync($"{Api}/schedules/{schedule}/active", new { active }, Json);

    // ---- posts ----

    /// <summary>Every post from two days ago to three weeks ahead (any status), optionally of one schedule.</summary>
    public async Task<List<PostDto>> PostsAsync(Guid? schedule = null)
    {
        var from = Uri.EscapeDataString(Now.AddDays(-2).ToString("O"));
        var to = Uri.EscapeDataString(Now.AddDays(21).ToString("O"));
        var all = (await Owner.GetFromJsonAsync<List<PostDto>>($"{Api}/posts?from={from}&to={to}", Json))!;
        return schedule is null ? all : all.Where(p => p.ScheduleId == schedule).ToList();
    }

    public async Task<PostDto> PostAsync(Guid id) => (await PostsAsync()).Single(p => p.Id == id);

    /// <summary>A real post to a link of the set, due now (the test page).</summary>
    public async Task<PostDto> TestPostAsync(int link = 0, string? text = null) =>
        await (await Owner.PostAsJsonAsync($"{Api}/test-post",
            new { linkSetId = Set.Id, linkId = Link(link).Id, collectionId = Collection.Id, text }, Json)).ReadAsync<PostDto>();

    // ---- the browser ----

    public async Task<JobDto?> ClaimAsync()
    {
        var res = await Device.PostAsync("/api/device/jobs/claim", null);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        return await res.ReadAsync<JobDto>();
    }

    public async Task<PostDto> ReportAsync(
        Guid post, bool ok = true, bool awaitingApproval = false, bool needsLogin = false, bool blocked = false, string? error = null,
        string? postUrl = null, string? shot = null) =>
        await (await Device.PostAsJsonAsync($"/api/device/jobs/{post}/result", new { ok, awaitingApproval, needsLogin, blocked, error, postUrl, shot }, Json))
            .ReadAsync<PostDto>();

    /// <summary>The result of a bump job.</summary>
    public async Task<HttpResponseMessage> ReportBumpAsync(Guid bump, bool ok = true, bool needsLogin = false, bool blocked = false, string? error = null) =>
        await Device.PostAsJsonAsync($"/api/device/bumps/{bump}/result", new { ok, needsLogin, blocked, error }, Json);

    /// <summary>Claims the next post and reports it; null when there was none.</summary>
    public async Task<PostDto?> RunNextAsync(bool ok = true, bool blocked = false, bool needsLogin = false, bool awaitingApproval = false, string? error = null)
    {
        var job = await ClaimAsync();
        return job is null ? null : await ReportAsync(job.PostId, ok, awaitingApproval, needsLogin, blocked, error ?? (ok ? null : "ผิดพลาด"));
    }

    public async Task<DeviceStatusDto> HeartbeatAsync() =>
        await (await Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "2.2.0" }, Json)).ReadAsync<DeviceStatusDto>();

    public async Task<DeviceDto> DeviceAsync() =>
        (await Owner.GetFromJsonAsync<List<DeviceDto>>($"{Api}/devices", Json))!.Single();

    // ---- settings and events ----

    public async Task<AntiBanDto> AntiBanAsync() =>
        (await Owner.GetFromJsonAsync<EngineSettingsDto>($"{Api}/engine", Json))!.AntiBan;

    public async Task SetAntiBanAsync(Func<AntiBanDto, AntiBanDto> change)
    {
        var next = change(await AntiBanAsync());
        (await Owner.PutAsJsonAsync($"{Api}/engine/anti-ban", next, Json)).EnsureSuccessStatusCode();
    }

    public Task SetAdvancedAsync(Func<AdvancedAntiBanDto, AdvancedAntiBanDto> change) =>
        SetAntiBanAsync(a => a with { Advanced = change(a.Advanced) });

    public async Task<long> HeadAsync() =>
        (await Owner.GetFromJsonAsync<DeviceEventsPageDto>($"{Api}/events?after=0&take=1", Json))!.Head;

    /// <summary>Events of a type after a Seq (take the head before the action).</summary>
    public async Task<List<DeviceEventDto>> EventsAsync(string type, long after) =>
        (await Owner.GetFromJsonAsync<DeviceEventsPageDto>($"{Api}/events?after={after}&take=500", Json))!.Events.Where(e => e.Type == type).ToList();

    public async Task<LinkSetDto> RefreshSetAsync() =>
        Set = (await Owner.LinkSetsAsync(Ws)).Single(s => s.Id == Set.Id);

    public async Task<SetLinkDto> UpdateLinkAsync(int link, bool? enabled = null, int? dailyMax = null)
    {
        var l = Link(link);
        var res = await Owner.PutAsJsonAsync($"{Api}/link-sets/{Set.Id}/links/{l.Id}",
            new { name = l.Name, url = l.Url, code = l.Code, dailyMax = dailyMax ?? l.DailyMax, enabled = enabled ?? l.Enabled }, Json);
        var updated = await res.ReadAsync<SetLinkDto>();
        await RefreshSetAsync();
        return updated;
    }

    /// <summary>The set as the API has it now (a link's health and switch change while the engine runs).</summary>
    public async Task<SetLinkDto> LinkNowAsync(int link) => (await RefreshSetAsync()).Links[link];
}

internal static class EngineTestSupport
{
    /// <summary>
    /// A shop: a signed-up owner on a plan, a paired browser, a collection with <paramref name="posts"/> posts and a link
    /// set with <paramref name="links"/> groups (named "กลุ่ม i", coded "Ci").
    /// </summary>
    public static async Task<Shop> ShopAsync(this ApiFactory factory, string plan = "pro", int links = 2, int posts = 3, string? footer = null) =>
        await ShopAsync(factory.Clock, factory, plan, links, posts, footer);

    private static async Task<Shop> ShopAsync(TestClock clock, ApiFactory factory, string plan, int links, int posts, string? footer)
    {
        var (owner, auth, ws) = await factory.SignUpAsync(plan);
        var (device, pair) = await factory.PairDeviceAsync(owner, ws);
        return await BuildShopAsync(clock, owner, auth, ws, device, pair, links, posts, footer);
    }

    /// <summary>The same for a host made with <c>WithWebHostBuilder</c> (its own services, the same database).</summary>
    public static async Task<Shop> ShopAsync(
        this WebApplicationFactory<Program> host, TestClock clock, string plan = "pro", int links = 2, int posts = 3, string? footer = null)
    {
        var json = ApiFactory.Json;
        var client = host.CreateClient();
        var email = $"u{Guid.NewGuid():N}@shop.co";
        var res = await client.PostAsJsonAsync("/api/auth/signup", new { email, password = "password1" }, json);
        var auth = await res.ReadAsync<AuthResultDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        if (plan != "free")
        {
            var admin = host.CreateClient();
            var login = await admin.PostAsJsonAsync("/api/auth/login", new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword }, json);
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.ReadAsync<AuthResultDto>()).Token);
            (await admin.PutAsJsonAsync($"/api/admin/customers/{auth.User.Id}/plan", new { plan }, json)).EnsureSuccessStatusCode();
        }
        var ws = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", json))![0].Id;
        var code = await (await client.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).ReadAsync<PairingCodeDto>();
        var device = host.CreateClient();
        var pair = await (await device.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "Shop PC", browser = "Chrome", version = "2.2.0" }, json))
            .ReadAsync<PairResultDto>();
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        return await BuildShopAsync(clock, client, auth, ws, device, pair, links, posts, footer);
    }

    private static async Task<Shop> BuildShopAsync(
        TestClock clock, HttpClient owner, AuthResultDto auth, Guid ws, HttpClient device, PairResultDto pair, int links, int posts, string? footer)
    {
        var collection = await owner.CreateCollectionAsync(ws, "โปรโมชัน");
        if (footer is not null)
            (await owner.PutAsJsonAsync($"/api/workspaces/{ws}/collections/{collection.Id}",
                new { name = collection.Name, description = "", icon = (string?)null, settings = WorkflowTestSupport.Settings(footer: footer) }, ApiFactory.Json))
                .EnsureSuccessStatusCode();
        for (var i = 1; i <= posts; i++) await owner.AddPostAsync(ws, collection.Id, $"โพสต์ {i}");
        var set = await owner.CreateLinkSetAsync(ws, "กลุ่มขายของ", pair.AccountId);
        for (var i = 0; i < links; i++)
            await owner.AddLinkAsync(ws, set.Id, $"https://www.facebook.com/groups/shop{i}-{Guid.NewGuid():N}"[..48], $"C{i}", $"กลุ่ม {i}");
        return new Shop
        {
            Clock = clock, Owner = owner, Ws = ws, Device = device, Pair = pair, Auth = auth,
            Collection = (await owner.CollectionsAsync(ws)).Single(c => c.Id == collection.Id),
            Set = (await owner.LinkSetsAsync(ws)).Single(s => s.Id == set.Id),
        };
    }

    /// <summary>
    /// The clock time in the schedule's calendar <paramref name="ahead"/> from now, as "HH:mm", and whether today's
    /// occurrence of that time is still to come (it is not when the time wrapped past midnight).
    /// </summary>
    public static (string Time, bool TodayIsAhead) SlotAhead(DateTimeOffset now, int offsetMinutes, TimeSpan ahead)
    {
        var local = now.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        var slot = local.Add(ahead);
        return (slot.ToString("HH:mm"), slot.Date == local.Date);
    }

    public static string ToLocalDay(DateTimeOffset at, int offsetMinutes) => at.ToOffset(TimeSpan.FromMinutes(offsetMinutes)).ToString("yyyy-MM-dd");
}
