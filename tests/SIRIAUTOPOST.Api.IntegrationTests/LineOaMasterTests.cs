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
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.DeleteAsync($"{url}/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{url}/{two.Id}")).StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<List<LineOaDto>>(url, Json))!);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{url}/{one.Id}/sync", null)).StatusCode);
    }

    [Fact]
    public async Task A_plan_without_line_chats_cannot_use_the_list()
    {
        var (owner, _, ws) = await factory.SignUpAsync("free");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/workspaces/{ws}/line/oas")).StatusCode);
    }
}
