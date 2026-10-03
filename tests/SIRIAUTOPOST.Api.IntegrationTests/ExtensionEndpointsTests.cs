using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The extension's own campaigns shown and edited in the web app: settings sync, media, live state, log and commands.
[Collection(ApiCollection.Name)]
public class ExtensionEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = ApiFactory.Json;
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private sealed record Paired(HttpClient Owner, Guid Ws, HttpClient Device, PairResultDto Pair)
    {
        public string Base => $"/api/workspaces/{Ws}/devices/{Pair.DeviceId}";
    }

    private async Task<Paired> PairAsync(string? plan = null)
    {
        var (owner, _, ws) = await factory.SignUpAsync(plan);
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content
            .ReadFromJsonAsync<PairingCodeDto>(Json))!;
        var device = factory.CreateClient();
        var pair = (await (await device.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "Shop PC" }, Json)).Content
            .ReadFromJsonAsync<PairResultDto>(Json))!;
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        return new Paired(owner, ws, device, pair);
    }

    private static object Settings(string groupUrl, params string[] imageIds) => new
    {
        version = 2,
        global = new { minGapMin = 2, telegram = new { enabled = false } },
        campaigns = new[]
        {
            new
            {
                id = "c1",
                name = "ชุดที่ 1",
                enabled = true,
                groups = new[] { new { url = groupUrl, name = "", text = "#Jan240015", enabled = true, dailyMax = 1 } },
                posts = new[] { new { id = "p1", text = "{สวัสดี|หวัดดี}ค่ะ", imageIds, groupUrls = Array.Empty<string>() } },
                leadImageIds = Array.Empty<string>(),
                config = new { groupDelayMin = 3, groupDelayMax = 8 },
            },
        },
    };

    private static async Task<DeviceSyncDto> SyncAsync(HttpClient device, object? state = null, object[]? logs = null, bool take = true)
    {
        var res = await device.PostAsJsonAsync("/api/device/sync", new { version = "2.1.0", state, logs, takeCommands = take }, Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<DeviceSyncDto>(Json))!;
    }

    [Fact]
    public async Task The_device_uploads_its_settings_and_the_web_app_reads_and_edits_them()
    {
        var p = await PairAsync();
        var first = await SyncAsync(p.Device);
        Assert.Equal((0, false), (first.Revision, first.HasContent));
        var empty = (await p.Owner.GetFromJsonAsync<ExtensionConfigDto>($"{p.Base}/config", Json))!;
        Assert.Null(empty.Settings);

        // First sync with an empty server: the extension pushes what it has, images first.
        var missing = (await (await p.Device.PostAsJsonAsync("/api/device/images/missing", new { ids = new[] { "img1", "bad id!" } })).Content
            .ReadFromJsonAsync<MissingImagesDto>(Json))!;
        Assert.Equal(["img1"], missing.Missing);
        Assert.Equal(HttpStatusCode.NoContent,
            (await p.Device.PutAsJsonAsync("/api/device/images/img1", new { name = "a.png", type = "image/png", data = Png })).StatusCode);
        var saved = await p.Device.PutAsJsonAsync("/api/device/config", new { settings = Settings("https://www.facebook.com/groups/1/", "img1"), baseRevision = 0 }, Json);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<ConfigSavedDto>(Json))!.Revision);

        var web = (await p.Owner.GetFromJsonAsync<ExtensionConfigDto>($"{p.Base}/config", Json))!;
        Assert.Equal((1, true, true), (web.Revision, web.HasContent, web.UpdatedByDevice));
        var camp = web.Settings!.Value.GetProperty("campaigns")[0];
        Assert.Equal("ชุดที่ 1", camp.GetProperty("name").GetString());
        Assert.Equal("#Jan240015", camp.GetProperty("groups")[0].GetProperty("text").GetString());

        var img = await p.Owner.GetAsync($"/api/workspaces/{p.Ws}/extension-images/img1");
        Assert.Equal("image/png", img.Content.Headers.ContentType!.MediaType);

        // The web app edits revision 1; the device sees revision 2 on its next sync and pulls it.
        var edit = await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = Settings("https://www.facebook.com/groups/2/", "img1"), baseRevision = 1 }, Json);
        Assert.Equal(2, (await edit.Content.ReadFromJsonAsync<ConfigSavedDto>(Json))!.Revision);
        Assert.Equal(2, (await SyncAsync(p.Device)).Revision);
        var pulled = (await p.Device.GetFromJsonAsync<ExtensionConfigDto>("/api/device/config", Json))!;
        Assert.Equal(2, pulled.Revision);
        Assert.False(pulled.UpdatedByDevice);
        var rec = (await p.Device.GetFromJsonAsync<ExtensionImageDto>("/api/device/images/img1", Json))!;
        Assert.Equal((Png, "image/png"), (rec.Data, rec.Type));

        // The device still at revision 1 cannot overwrite the web's edit; null overwrites on purpose.
        var stale = await p.Device.PutAsJsonAsync("/api/device/config", new { settings = Settings("https://www.facebook.com/groups/3/"), baseRevision = 1 }, Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var same = await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = Settings("https://www.facebook.com/groups/2/", "img1"), baseRevision = (int?)null }, Json);
        Assert.Equal(2, (await same.Content.ReadFromJsonAsync<ConfigSavedDto>(Json))!.Revision); // identical: no new revision

        var invalid = await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = new { groups = 1 }, baseRevision = 2 }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task Images_no_settings_use_go_away_after_the_grace_period()
    {
        var p = await PairAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/extension-images/web1", new { name = "w.png", type = "image/png", data = Png })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await p.Owner.PutAsJsonAsync($"/api/workspaces/{p.Ws}/extension-images/web2", new { name = "x.txt", type = "text/plain", data = "data:text/plain;base64,aGk=" })).StatusCode);
        await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = Settings("https://www.facebook.com/groups/1/", "web1"), baseRevision = 0 }, Json);
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.GetAsync($"/api/workspaces/{p.Ws}/extension-images/web1")).StatusCode);

        using (factory.Clock.Advance(TimeSpan.FromHours(2)))
        {
            // Still used: kept. Then the post loses its image: deleted on that save.
            await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = Settings("https://www.facebook.com/groups/9/", "web1"), baseRevision = 1 }, Json);
            Assert.Equal(HttpStatusCode.OK, (await p.Owner.GetAsync($"/api/workspaces/{p.Ws}/extension-images/web1")).StatusCode);
            await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = Settings("https://www.facebook.com/groups/9/"), baseRevision = 2 }, Json);
            Assert.Equal(HttpStatusCode.NotFound, (await p.Owner.GetAsync($"/api/workspaces/{p.Ws}/extension-images/web1")).StatusCode);
        }
    }

    [Fact]
    public async Task State_logs_and_commands_go_between_the_device_and_the_web_app()
    {
        var p = await PairAsync();
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await SyncAsync(p.Device, new { running = true, campaigns = new { c1 = new { round = 2 } } },
            [new { t = t0, level = "info", msg = "เริ่มทำงาน" }, new { t = t0 + 1, level = "success", msg = "โพสต์สำเร็จ" }]);
        // Lines already sent are not stored twice.
        await SyncAsync(p.Device, null, [new { t = t0 + 1, level = "success", msg = "โพสต์สำเร็จ" }, new { t = t0 + 2, level = "warn", msg = "พัก" }]);

        var live = (await p.Owner.GetFromJsonAsync<DeviceLiveDto>($"{p.Base}/live", Json))!;
        Assert.True(live.Online);
        Assert.Equal("2.1.0", live.Version);
        Assert.True(live.State!.Value.GetProperty("running").GetBoolean());
        Assert.Equal(["เริ่มทำงาน", "โพสต์สำเร็จ", "พัก"], live.Logs.Select(l => l.Msg));

        var bad = await p.Owner.PostAsJsonAsync($"{p.Base}/commands", new { cmd = "rm -rf" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var sent = (await (await p.Owner.PostAsJsonAsync($"{p.Base}/commands",
            new { cmd = "testPost", args = new { campaignId = "c1", url = "https://www.facebook.com/groups/1/" } }, Json)).Content
            .ReadFromJsonAsync<DeviceCommandDto>(Json))!;
        Assert.Equal(CommandStatus.Pending, sent.Status);

        Assert.Empty((await SyncAsync(p.Device, take: false)).Commands); // heartbeat without taking commands
        var sync = await SyncAsync(p.Device);
        var cmd = Assert.Single(sync.Commands);
        Assert.Equal(("testPost", "c1"), (cmd.Cmd, cmd.Args.GetProperty("campaignId").GetString()));
        // The call that carried it may have been cut short on the device, so a plain sync hands it out again
        // until a result comes in (the device runs each id once). A held call only waits for new commands.
        Assert.Equal(cmd.Id, Assert.Single((await SyncAsync(p.Device)).Commands).Id);
        Assert.Equal(CommandStatus.Sent, (await p.Owner.GetFromJsonAsync<DeviceCommandDto>($"{p.Base}/commands/{cmd.Id}", Json))!.Status);

        Assert.Equal(HttpStatusCode.NoContent,
            (await p.Device.PostAsJsonAsync($"/api/device/commands/{cmd.Id}/result", new { result = new { ok = false, error = "ยังไม่ได้ล็อกอิน" } }, Json)).StatusCode);
        var done = (await p.Owner.GetFromJsonAsync<DeviceCommandDto>($"{p.Base}/commands/{cmd.Id}", Json))!;
        Assert.Equal(CommandStatus.Done, done.Status);
        Assert.Equal("ยังไม่ได้ล็อกอิน", done.Result!.Value.GetProperty("error").GetString());

        Assert.Empty((await SyncAsync(p.Device)).Commands); // reported: not handed out again

        // A command that was handed out and never answered is given up on after 10 minutes, like one nobody took.
        var lost = (await (await p.Owner.PostAsJsonAsync($"{p.Base}/commands", new { cmd = "stop" }, Json)).Content
            .ReadFromJsonAsync<DeviceCommandDto>(Json))!;
        Assert.Equal(lost.Id, Assert.Single((await SyncAsync(p.Device)).Commands).Id);
        using (factory.Clock.Advance(TimeSpan.FromMinutes(11)))
        {
            Assert.Empty((await SyncAsync(p.Device)).Commands);
            Assert.Equal(CommandStatus.Expired, (await p.Owner.GetFromJsonAsync<DeviceCommandDto>($"{p.Base}/commands/{lost.Id}", Json))!.Status);
        }

        // A command nobody takes within 10 minutes never runs.
        var late = (await (await p.Owner.PostAsJsonAsync($"{p.Base}/commands", new { cmd = "start" }, Json)).Content
            .ReadFromJsonAsync<DeviceCommandDto>(Json))!;
        using (factory.Clock.Advance(TimeSpan.FromMinutes(11)))
        {
            Assert.Equal(CommandStatus.Expired, (await p.Owner.GetFromJsonAsync<DeviceCommandDto>($"{p.Base}/commands/{late.Id}", Json))!.Status);
            Assert.Empty((await SyncAsync(p.Device)).Commands);
        }

        // Clearing the log empties the server copy and asks the device to empty its own.
        Assert.Equal(HttpStatusCode.NoContent, (await p.Owner.DeleteAsync($"{p.Base}/logs")).StatusCode);
        Assert.Empty((await p.Owner.GetFromJsonAsync<DeviceLiveDto>($"{p.Base}/live", Json))!.Logs);
        Assert.Equal("clearLogs", Assert.Single((await SyncAsync(p.Device)).Commands).Cmd);
    }

    [Fact]
    public async Task The_web_app_renames_a_browser_and_pauses_its_jobs()
    {
        var p = await PairAsync();
        var devUrl = $"/api/workspaces/{p.Ws}/devices/{p.Pair.DeviceId}";
        var res = await p.Owner.PutAsJsonAsync(devUrl, new { name = "คอมหน้าร้าน", jobsPaused = true }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dev = (await res.Content.ReadFromJsonAsync<DeviceDto>(Json))!;
        Assert.Equal(("คอมหน้าร้าน", true), (dev.Name, dev.JobsPaused));
        var accounts = (await p.Owner.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{p.Ws}/accounts", Json))!;
        Assert.Equal("Facebook · คอมหน้าร้าน", accounts.Single(a => a.Id == p.Pair.AccountId).Name);

        // The extension learns it on its heartbeat and gets no job.
        var hb = (await (await p.Device.PostAsJsonAsync("/api/device/heartbeat", new { version = "2.2.0" })).Content
            .ReadFromJsonAsync<DeviceStatusDto>(Json))!;
        Assert.Equal(("คอมหน้าร้าน", true), (hb.DeviceName, hb.JobsPaused));
        Assert.Equal(HttpStatusCode.NoContent, (await p.Device.PostAsync("/api/device/jobs/claim", null)).StatusCode);

        // null keeps a value.
        dev = (await (await p.Owner.PutAsJsonAsync(devUrl, new { jobsPaused = false }, Json)).Content.ReadFromJsonAsync<DeviceDto>(Json))!;
        Assert.Equal(("คอมหน้าร้าน", false), (dev.Name, dev.JobsPaused));
        var tooLong = await p.Owner.PutAsJsonAsync(devUrl, new { name = new string('ก', 81) }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Other_workspaces_and_devices_cannot_reach_it()
    {
        var p = await PairAsync();
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{p.Base}/config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await p.Owner.GetAsync($"/api/workspaces/{p.Ws}/devices/{Guid.NewGuid()}/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/device/sync", new { takeCommands = true })).StatusCode);
    }

    private static object WithToken(string token) => new
    {
        version = 2,
        global = new { minGapMin = 2, telegram = new { enabled = true, botToken = token, chatId = "-100123" } },
        campaigns = new[] { new { id = "c1", name = "แก้โดยทีม", enabled = true, groups = Array.Empty<object>(), posts = Array.Empty<object>(), leadImageIds = Array.Empty<string>() } },
    };

    private static string TokenOf(ExtensionConfigDto c) => c.Settings!.Value.GetProperty("global").GetProperty("telegram").GetProperty("botToken").GetString()!;

    [Fact]
    public async Task The_telegram_bot_token_is_only_for_workspace_admins_and_other_roles_cannot_change_it()
    {
        var p = await PairAsync("agency"); // seats for a team
        async Task<HttpClient> Member(string role)
        {
            var (client, auth, _) = await factory.SignUpAsync();
            (await p.Owner.PostAsJsonAsync($"/api/workspaces/{p.Ws}/members", new { email = auth.User.Email, role }, Json)).EnsureSuccessStatusCode();
            return client;
        }
        var admin = await Member("admin");
        var editor = await Member("editor");
        var viewer = await Member("viewer");

        // The owner (or the device) sets the token.
        Assert.Equal(HttpStatusCode.OK, (await p.Owner.PutAsJsonAsync($"{p.Base}/config", new { settings = WithToken("123:SECRET"), baseRevision = (int?)null }, Json)).StatusCode);
        async Task<ExtensionConfigDto> ReadAsync(HttpClient c) => (await c.GetFromJsonAsync<ExtensionConfigDto>($"{p.Base}/config", Json))!;
        Assert.Equal("123:SECRET", TokenOf(await ReadAsync(p.Owner)));
        Assert.Equal("123:SECRET", TokenOf(await ReadAsync(admin)));
        Assert.Equal("", TokenOf(await ReadAsync(editor)));
        Assert.Equal("", TokenOf(await ReadAsync(viewer)));
        // The chat id and the switch are not secret.
        var seen = (await ReadAsync(viewer)).Settings!.Value.GetProperty("global").GetProperty("telegram");
        Assert.Equal(("-100123", true), (seen.GetProperty("chatId").GetString(), seen.GetProperty("enabled").GetBoolean()));

        // An editor edits a campaign with the empty token they were shown, and tries to set one: the stored token stays.
        var rev = (await ReadAsync(editor)).Revision;
        (await editor.PutAsJsonAsync($"{p.Base}/config", new { settings = WithToken(""), baseRevision = rev }, Json)).EnsureSuccessStatusCode();
        (await editor.PutAsJsonAsync($"{p.Base}/config", new { settings = WithToken("evil:TOKEN"), baseRevision = rev + 1 }, Json)).EnsureSuccessStatusCode();
        var after = await ReadAsync(p.Owner);
        Assert.Equal("123:SECRET", TokenOf(after));
        Assert.Equal("แก้โดยทีม", after.Settings!.Value.GetProperty("campaigns")[0].GetProperty("name").GetString());

        // An admin may change it, and the device (the browser that holds the token) keeps receiving it.
        (await admin.PutAsJsonAsync($"{p.Base}/config", new { settings = WithToken("456:NEW"), baseRevision = after.Revision }, Json)).EnsureSuccessStatusCode();
        Assert.Equal("456:NEW", TokenOf(await ReadAsync(p.Owner)));
        var own = (await p.Device.GetFromJsonAsync<ExtensionConfigDto>("/api/device/config", Json))!;
        Assert.Equal("456:NEW", TokenOf(own));
    }
}
