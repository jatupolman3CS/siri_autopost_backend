using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The master list of LINE Official Accounts: an OA is added by its chat.line.biz address, the browser is told to read its
// customers, and the customers are kept apart per OA.
[Collection(ApiCollection.Name)]
public class LineOaMasterTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = ApiFactory.Json;

    [Fact]
    public async Task An_oa_is_added_by_its_address_and_its_customers_are_read_per_oa()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);
        var url = $"/api/workspaces/{ws}/line/oas";

        var added = await owner.PostAsJsonAsync(url, new { url = "https://chat.line.biz/Uoaone12345/chat", name = "OA One" }, Json);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var one = (await added.Content.ReadFromJsonAsync<LineOaDto>(Json))!;
        Assert.Equal("Uoaone12345", one.BotId);
        Assert.Equal("https://chat.line.biz/Uoaone12345", one.Url);
        Assert.Equal(0, one.Chats);

        // The browser is told which OA to read.
        var cmds = await factory.WithDbAsync(db => db.DeviceCommands.Where(c => c.Cmd == "syncLineChats").Select(c => c.Args).ToListAsync());
        Assert.Contains(cmds, a => a.Contains("Uoaone12345"));

        // The same OA twice is refused; a second OA is fine.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(url, new { url = "chat.line.biz/Uoaone12345" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(url, new { url = "https://example.com/x" }, Json)).StatusCode);
        var two = (await (await owner.PostAsJsonAsync(url, new { url = "https://chat.line.biz/Uoatwo12345" }, Json)).Content.ReadFromJsonAsync<LineOaDto>(Json))!;

        // Each OA gets its own customers from the one browser.
        foreach (var (bot, id, name) in new[] { ("Uoaone12345", "U11111111111111111111111111111111", "สมชาย"), ("Uoatwo12345", "U22222222222222222222222222222222", "สมหญิง") })
        {
            Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/device/heartbeat",
                new { version = "2.5.0", line = new { ready = true, botId = bot, oaName = bot } }, Json)).StatusCode);
            var sync = await device.PutAsJsonAsync("/api/device/line/chats",
                new { botId = bot, oaName = bot, complete = true, chats = new[] { new { userId = id, name } } }, Json);
            Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        }

        var list = (await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!;
        Assert.Equal(2, list.Count);
        Assert.All(list, o => Assert.Equal(1, o.Chats));
        // The browser works for the OA it reported last; "use this OA" from the web tells it to switch.
        Assert.False(list.Single(o => o.Id == one.Id).Active);
        Assert.True(list.Single(o => o.Id == two.Id).Active);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{url}/{one.Id}/use", null)).StatusCode);
        var useCmds = await factory.WithDbAsync(db => db.DeviceCommands.Where(c => c.Cmd == "lineUseOa").Select(c => c.Args).ToListAsync());
        Assert.Contains(useCmds, a => a.Contains("Uoaone12345"));
        // One browser with two OAs still lists as one device (it once crashed on the second Line account).
        var devs = (await owner.GetFromJsonAsync<List<DeviceDto>>($"/api/workspaces/{ws}/devices", Json))!;
        Assert.Single(devs);
        var onlyOne = (await owner.GetFromJsonAsync<LineChatPageDto>($"/api/workspaces/{ws}/line/chats?accountId={one.Id}", Json))!;
        var chat = Assert.Single(onlyOne.Items);
        Assert.Equal("สมชาย", chat.Name);
        Assert.StartsWith("https://chat.line.biz/Uoaone12345/chat/", chat.Url);

        // A LINE link set posts through one of them; it cannot be removed meanwhile.
        var set = await owner.PostAsJsonAsync($"/api/workspaces/{ws}/link-sets", new { name = "ชุด", channel = "line", postAsAccountId = one.Id }, Json);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        // Removing an OA a link set uses is allowed: the set is left with no OA instead of blocking the removal.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{url}/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{url}/{two.Id}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!);
        var setsAfter = (await owner.GetFromJsonAsync<List<LinkSetDto>>($"/api/workspaces/{ws}/link-sets", Json))!;
        Assert.Null(setsAfter.Single(x => x.Name == "ชุด").PostAsAccountId);
    }

    [Fact]
    public async Task An_oa_that_lost_its_browser_goes_back_to_the_only_one_when_it_is_used_or_read()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (_, pair) = await factory.PairDeviceAsync(owner, ws);
        var url = $"/api/workspaces/{ws}/line/oas";
        var oa = (await (await owner.PostAsJsonAsync(url, new { url = "https://chat.line.biz/Ulostone123" }, Json)).Content.ReadFromJsonAsync<LineOaDto>(Json))!;

        // "Change LINE OA" / unbinding leaves the account without a browser.
        await factory.WithDbAsync(async db =>
        {
            var acc = await db.Accounts.FirstAsync(a => a.Id == oa.Id);
            acc.Disconnect();
            await db.SaveChangesAsync();
        });
        Assert.Null((await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!.Single().DeviceId);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{url}/{oa.Id}/use", null)).StatusCode);
        Assert.Equal(pair.DeviceId, (await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!.Single().DeviceId);
    }

    [Fact]
    public async Task A_heartbeat_without_an_oa_name_keeps_the_name_and_a_real_name_replaces_it()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);
        var url = $"/api/workspaces/{ws}/line/oas";
        var oa = (await (await owner.PostAsJsonAsync(url, new { url = "https://chat.line.biz/Unamed12345", name = "ร้านดอกไม้" }, Json)).Content.ReadFromJsonAsync<LineOaDto>(Json))!;
        Assert.Equal("LINE · ร้านดอกไม้", oa.Name);

        // The browser could not read the name of the OA it is on: the name the shop gave stays (it was once reset to the id).
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/device/heartbeat",
            new { version = "2.5.0", line = new { ready = true, botId = "Unamed12345", oaName = "" } }, Json)).StatusCode);
        Assert.Equal("LINE · ร้านดอกไม้", (await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!.Single().Name);

        // The name of the OA's own page replaces it.
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/device/heartbeat",
            new { version = "2.5.0", line = new { ready = true, botId = "Unamed12345", oaName = "ร้านดอกไม้สาขา 2" } }, Json)).StatusCode);
        Assert.Equal("LINE · ร้านดอกไม้สาขา 2", (await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!.Single().Name);
    }

    [Fact]
    public async Task A_roster_with_odd_text_and_a_local_time_is_still_taken()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/device/heartbeat",
            new { version = "2.5.0", line = new { ready = true, botId = "Uoddroster1", oaName = "OA" } }, Json)).StatusCode);

        // A NUL in a name and a time that carries the browser's offset each used to be a 500 for the whole roster.
        var res = await device.PutAsJsonAsync("/api/device/line/chats", new
        {
            botId = "Uoddroster1", oaName = "OA", complete = true,
            chats = new[]
            {
                new { userId = "U11111111111111111111111111111111", name = "สม ชาย", lastMessageAt = "2026-10-20T17:00:00+07:00" },
                new { userId = "U22222222222222222222222222222222", name = "มานี", lastMessageAt = "2026-10-20" },
            },
        }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var oas = (await owner.GetFromJsonAsync<List<LineOaDto>>($"/api/workspaces/{ws}/line/oas", Json))!;
        Assert.Equal(2, oas.Single().Chats);
    }

    [Fact]
    public async Task A_roster_of_the_real_size_with_empty_names_and_repeated_customers_is_taken_whole()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (device, _) = await factory.PairDeviceAsync(owner, ws);
        Assert.Equal(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/device/heartbeat",
            new { version = "2.5.0", line = new { ready = true, botId = "Ubigroster12", oaName = "OA" } }, Json)).StatusCode);

        // What the contacts page really gave (852 rows): some without a name, a few customers listed twice, labels and dates.
        string Uid(int i) => "U" + i.ToString("x").PadLeft(32, '0');
        var chats = Enumerable.Range(1, 852).Select(i => new
        {
            userId = Uid(i <= 843 ? i : i - 843), // the last 9 repeat the first 9
            name = i % 45 == 0 ? "" : $"ลูกค้า {i} 😀",
            labels = i % 7 == 0 ? new[] { "VIP", "ใหม่" } : Array.Empty<string>(),
            lastMessageAt = "2026-08-01T00:00:00+07:00",
        }).ToArray();
        var res = await device.PutAsJsonAsync("/api/device/line/chats",
            new { botId = "Ubigroster12", oaName = "OA", complete = true, chats }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var oas = (await owner.GetFromJsonAsync<List<LineOaDto>>($"/api/workspaces/{ws}/line/oas", Json))!;
        Assert.Equal(843, oas.Single().Chats);
    }

    [Fact]
    public async Task A_plan_without_line_chats_cannot_use_the_list()
    {
        var (owner, _, ws) = await factory.SignUpAsync("free");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/workspaces/{ws}/line/oas")).StatusCode);
    }
}
