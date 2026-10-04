using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Notification settings: plan gate, roles, token redaction, test message, chat lookup, start/stop hook.
[Collection(ApiCollection.Name)]
public class NotificationsEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = WorkflowTestSupport.Json;

    private static string Base(Guid ws) => $"/api/workspaces/{ws}/notifications";

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}";

    internal static object Events(
        bool success = false, bool fail = true, bool shot = true, bool round = true, bool startStop = true, bool block = true, bool offline = true,
        bool quota = false) =>
        new { success, fail, shot, round, startStop, block, offline, quota };

    /// <summary>A complete request body: the API wants every part (a null token keeps the stored one).</summary>
    internal static object Settings(
        string? tgToken = null, string tgChat = "", bool tgOn = false, string? lineToken = null, string lineTo = "", bool lineOn = false,
        string channel = "tg", object? events = null, object[]? sets = null, bool commandsOn = false, string commandsUsers = "") =>
        new
        {
            telegram = new { on = tgOn, token = tgToken, chatId = tgChat },
            line = new { on = lineOn, token = lineToken, to = lineTo },
            channel,
            events = events ?? Events(),
            sets = sets ?? [],
            commandsOn,
            commandsUsers,
        };

    private static async Task<NotificationSettingsDto> PutAsync(HttpClient client, Guid ws, object body) =>
        await (await client.PutAsJsonAsync(Base(ws), body, Json)).ReadAsync<NotificationSettingsDto>();

    private static async Task<NotificationSettingsDto> GetAsync(HttpClient client, Guid ws) =>
        (await client.GetFromJsonAsync<NotificationSettingsDto>(Base(ws), Json))!;

    private static async Task AssertProblemAsync(HttpResponseMessage res, HttpStatusCode status, string? title = null)
    {
        Assert.Equal(status, res.StatusCode);
        if (title is not null) Assert.Equal(title, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title);
    }

    // ---- defaults, plan gate and roles ----

    [Fact]
    public async Task A_workspace_on_any_plan_reads_the_default_settings()
    {
        var (free, _, ws) = await factory.SignUpAsync();

        var n = await GetAsync(free, ws);

        Assert.Equal((false, false, null, null), (n.Telegram.On, n.Telegram.HasToken, n.Telegram.Token, n.Line.Token));
        Assert.Equal(NotifyChannel.Tg, n.Channel);
        Assert.Equal(new NotifyEventsDto(false, true, true, true, true, true, true, false), n.Events); // the design's defaults
        Assert.Empty(n.Sets);
        Assert.False(n.CommandsOn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("basic")]
    public async Task Plans_below_Pro_cannot_change_notifications(string? plan)
    {
        var (client, _, ws) = await factory.SignUpAsync(plan);

        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Settings("123:abc"), Json), HttpStatusCode.Forbidden, "ต้องใช้แผน Pro ขึ้นไป");
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "tg" }, Json), HttpStatusCode.Forbidden, "ต้องใช้แผน Pro ขึ้นไป");
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/telegram/chats", new { token = "123:abc" }, Json), HttpStatusCode.Forbidden);
        Assert.False((await GetAsync(client, ws)).Telegram.HasToken); // nothing was stored
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("agency")]
    public async Task Pro_and_Agency_save_notifications(string plan)
    {
        var (client, _, ws) = await factory.SignUpAsync(plan);

        var saved = await PutAsync(client, ws, Settings("123:abc", "-1001", tgOn: true, channel: "both"));

        Assert.Equal((true, true, "-1001", NotifyChannel.Both), (saved.Telegram.On, saved.Telegram.HasToken, saved.Telegram.ChatId, saved.Channel));
        Assert.Equal(JsonSerializer.Serialize(saved, Json), JsonSerializer.Serialize(await GetAsync(client, ws), Json));
    }

    [Fact]
    public async Task The_workspace_dto_says_which_plan_features_the_owner_has()
    {
        async Task<WorkspaceDto> Of(string? plan)
        {
            var (client, _, ws) = await factory.SignUpAsync(plan);
            return (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single(w => w.Id == ws);
        }

        Assert.Equal((false, false, false), ((await Of(null)).Notifications, (await Of("basic")).AutoReply, (await Of(null)).ClientReports));
        var pro = await Of("pro");
        Assert.Equal((true, true, false), (pro.Notifications, pro.AutoReply, pro.ClientReports));
        var agency = await Of("agency");
        Assert.Equal((true, true, true), (agency.Notifications, agency.AutoReply, agency.ClientReports));
    }

    [Fact]
    public async Task Viewers_read_and_only_admins_edit_with_the_owners_plan()
    {
        var team = await factory.TeamAsync(); // the owner is on Agency, so the plan gate passes for everyone

        Assert.Equal(HttpStatusCode.OK, (await team.Viewer.GetAsync(Base(team.Ws))).StatusCode);
        foreach (var client in new[] { team.Viewer, team.Editor })
        {
            await AssertProblemAsync(await client.PutAsJsonAsync(Base(team.Ws), Settings(), Json), HttpStatusCode.Forbidden);
            await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(team.Ws)}/test", new { channel = "tg" }, Json), HttpStatusCode.Forbidden);
            await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(team.Ws)}/telegram/chats", new { }, Json), HttpStatusCode.Forbidden);
        }
        Assert.Equal(HttpStatusCode.OK, (await team.Admin.PutAsJsonAsync(Base(team.Ws), Settings("123:abc", "-9"), Json)).StatusCode);

        var (stranger, _, _) = await factory.SignUpAsync("agency");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Base(team.Ws))).StatusCode);
    }

    // ---- tokens never come back ----

    [Fact]
    public async Task Tokens_are_kept_replaced_or_cleared_and_never_returned()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var (chat, lineTo) = (Unique("-100"), Unique("U"));

        var put = await client.PutAsJsonAsync(Base(ws), Settings("111:tg-secret-token", chat, tgOn: true, lineToken: "line-secret-token", lineTo: lineTo, lineOn: true), Json);
        var body = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("secret", await client.GetStringAsync(Base(ws)));
        var saved = JsonSerializer.Deserialize<NotificationSettingsDto>(body, Json)!;
        Assert.Equal((true, true, null, null), (saved.Telegram.HasToken, saved.Line.HasToken, saved.Telegram.Token, saved.Line.Token));

        // Sends a test message and returns the token the gateway was given.
        async Task<string> TestAsync(string channel, string target)
        {
            var result = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel }, Json)).ReadAsync<NotifyTestResultDto>();
            Assert.True(result.Ok, result.Message);
            return factory.Notifications.To(target).Last().Token;
        }

        // A null token keeps the stored one (the web app sends null when the person did not retype it).
        await PutAsync(client, ws, Settings(null, chat, tgOn: true, lineToken: null, lineTo: lineTo, lineOn: true, channel: "line"));
        Assert.Equal("111:tg-secret-token", await TestAsync("tg", chat));
        Assert.Equal("line-secret-token", await TestAsync("line", lineTo));

        // A new token replaces it.
        await PutAsync(client, ws, Settings("222:new-token", chat, tgOn: true, lineToken: null, lineTo: lineTo, lineOn: true));
        Assert.Equal("222:new-token", await TestAsync("tg", chat));

        // "" clears it: the flag goes and so does the ability to send.
        var cleared = await PutAsync(client, ws, Settings("", chat, tgOn: true, lineToken: null, lineTo: lineTo, lineOn: true));
        Assert.Equal((false, true), (cleared.Telegram.HasToken, cleared.Line.HasToken));
        var after = factory.Notifications.To(chat).Count;
        var failed = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "tg" }, Json)).ReadAsync<NotifyTestResultDto>();
        Assert.False(failed.Ok);
        Assert.Equal(after, factory.Notifications.To(chat).Count); // nothing was sent
    }

    [Fact]
    public async Task Only_what_is_filled_is_trimmed_and_checked()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");

        var saved = await PutAsync(client, ws, Settings("  123:abc  ", "  -55  ", commandsUsers: "  @somchai  "));

        Assert.Equal(("-55", "@somchai", true), (saved.Telegram.ChatId, saved.CommandsUsers, saved.Telegram.HasToken));
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Settings(new string('x', 201)), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Settings(tgChat: new string('1', 101)), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Settings(channel: "carrier_pigeon"), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Settings(channel: "default"), Json), HttpStatusCode.UnprocessableEntity); // the workspace has no parent to follow
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), new { channel = "tg" }, Json), HttpStatusCode.BadRequest); // parts missing
        Assert.Equal(saved.Telegram.ChatId, (await GetAsync(client, ws)).Telegram.ChatId); // bad requests changed nothing
    }

    // ---- rules of sets and groups ----

    [Fact]
    public async Task Rules_of_sets_and_groups_that_no_longer_exist_are_dropped_on_save()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var set = await client.CreateLinkSetAsync(ws);
        var link = await client.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/shop");
        var other = await client.AddLinkAsync(ws, set.Id, "https://www.facebook.com/groups/other");
        var (ghostSet, ghostLink) = (Guid.NewGuid(), Guid.NewGuid());

        object Body() => Settings("1:a", "-1", tgOn: true, sets:
        [
            new
            {
                linkSetId = set.Id, channel = "line", events = Events(success: true),
                groups = new Dictionary<string, object>
                {
                    [link.Id.ToString()] = new { channel = "off", events = (object?)null },
                    [other.Id.ToString()] = new { channel = "tg", events = Events(quota: true) },
                    [ghostLink.ToString()] = new { channel = "both", events = (object?)null },
                },
            },
            new { linkSetId = ghostSet, channel = "tg", events = (object?)null, groups = new Dictionary<string, object>() },
        ]);

        var saved = await PutAsync(client, ws, Body());

        var rule = Assert.Single(saved.Sets);
        Assert.Equal((set.Id, NotifyChannel.Line), (rule.LinkSetId, rule.Channel));
        Assert.True(rule.Events!.Success);
        Assert.Equal(new[] { link.Id.ToString(), other.Id.ToString() }.Order(), rule.Groups.Keys.Order());
        Assert.Equal(NotifyChannel.Off, rule.Groups[link.Id.ToString()].Channel);
        Assert.True(rule.Groups[other.Id.ToString()].Events!.Quota);

        // A group that is deleted later falls out the next time the settings are saved.
        (await client.DeleteAsync($"/api/workspaces/{ws}/link-sets/{set.Id}/links/{link.Id}")).EnsureSuccessStatusCode();
        var again = await PutAsync(client, ws, Body());
        Assert.Equal([other.Id.ToString()], Assert.Single(again.Sets).Groups.Keys);

        // Rules can be taken away by sending none.
        Assert.Empty((await PutAsync(client, ws, Settings("1:a", "-1", tgOn: true))).Sets);
    }

    [Fact]
    public async Task A_group_rule_with_a_key_that_is_not_an_id_is_refused()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var set = await client.CreateLinkSetAsync(ws);
        var body = Settings(sets:
        [
            new { linkSetId = set.Id, channel = "default", events = (object?)null, groups = new Dictionary<string, object> { ["not-a-guid"] = new { channel = "off", events = (object?)null } } },
        ]);

        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), body, Json), HttpStatusCode.UnprocessableEntity, "รหัสกลุ่มในกฎแจ้งเตือนไม่ถูกต้อง");
    }

    // ---- test message and chat lookup ----

    [Fact]
    public async Task The_test_message_goes_out_with_the_stored_settings_and_says_how_it_went()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var (chat, to) = (Unique("-100"), Unique("U"));
        await PutAsync(client, ws, Settings("123:abc", chat, lineToken: "line-token", lineTo: to)); // both channels still switched off: testing comes first

        var tg = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "tg" }, Json)).ReadAsync<NotifyTestResultDto>();
        var line = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "line" }, Json)).ReadAsync<NotifyTestResultDto>();

        Assert.Equal((true, true), (tg.Ok, line.Ok));
        var sentTg = Assert.Single(factory.Notifications.To(chat));
        Assert.Equal(("tg", "123:abc"), (sentTg.Channel, sentTg.Token));
        Assert.Contains("ทดสอบ", sentTg.Text);
        var sentLine = Assert.Single(factory.Notifications.To(to));
        Assert.Equal(("line", "line-token"), (sentLine.Channel, sentLine.Token));

        // A refusal comes back as ok:false with the gateway's reason (the request itself still succeeds).
        factory.Notifications.TelegramResult = GatewayResult.Failure("ส่งข้อความไปยัง Telegram ไม่สำเร็จ (401): Unauthorized");
        try
        {
            var failed = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "tg" }, Json)).ReadAsync<NotifyTestResultDto>();
            Assert.False(failed.Ok);
            Assert.Equal("ส่งข้อความไปยัง Telegram ไม่สำเร็จ (401): Unauthorized", failed.Message);
        }
        finally
        {
            factory.Notifications.TelegramResult = GatewayResult.Success;
        }
    }

    [Fact]
    public async Task The_test_message_needs_a_token_and_a_target_and_a_known_channel()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var before = factory.Notifications.All.Count;

        var nothing = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "tg" }, Json)).ReadAsync<NotifyTestResultDto>();
        Assert.False(nothing.Ok);
        Assert.Contains("Telegram", nothing.Message);

        await PutAsync(client, ws, Settings("123:abc", tgChat: "")); // a token but no chat
        Assert.False((await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "tg" }, Json)).ReadAsync<NotifyTestResultDto>()).Ok);
        var line = await (await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "line" }, Json)).ReadAsync<NotifyTestResultDto>();
        Assert.False(line.Ok);
        Assert.Contains("LINE", line.Message);
        Assert.Equal(before, factory.Notifications.All.Count); // nothing went out

        await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "sms" }, Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/test", new { channel = "" }, Json), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Telegram_chats_are_found_with_the_given_or_the_stored_token()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var token = Unique("1:t");
        factory.Notifications.Chats = t => t == token
            ? new TelegramChatsResult([new TelegramChat("-1001", "ร้านค้า"), new TelegramChat("42", "@somchai")], null)
            : new TelegramChatsResult([], "โทเคนไม่ถูกต้อง");
        try
        {
            // No token anywhere: nothing to look up.
            await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/telegram/chats", new { }, Json), HttpStatusCode.UnprocessableEntity);

            var withBody = await (await client.PostAsJsonAsync($"{Base(ws)}/telegram/chats", new { token = $"  {token} " }, Json)).ReadAsync<TelegramChatsDto>();
            Assert.Equal([("-1001", "ร้านค้า"), ("42", "@somchai")], withBody.Chats.Select(c => (c.Id, c.Title)));

            // The stored token is used when none is sent (an empty body works too).
            await PutAsync(client, ws, Settings(token, "-1"));
            var stored = await (await client.PostAsJsonAsync($"{Base(ws)}/telegram/chats", new { }, Json)).ReadAsync<TelegramChatsDto>();
            Assert.Equal(2, stored.Chats.Count);
            var noBody = await (await client.PostAsync($"{Base(ws)}/telegram/chats", null)).ReadAsync<TelegramChatsDto>();
            Assert.Equal(2, noBody.Chats.Count);
            Assert.Contains(token, factory.Notifications.Lookups);

            // Telegram refusing the token is a 422 that says why.
            await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/telegram/chats", new { token = "9:wrong" }, Json), HttpStatusCode.UnprocessableEntity, "โทเคนไม่ถูกต้อง");
            await AssertProblemAsync(await client.PostAsJsonAsync($"{Base(ws)}/telegram/chats", new { token = new string('x', 201) }, Json), HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Notifications.Chats = _ => new TelegramChatsResult([], null);
        }
    }

    // ---- the start / stop hook ----

    private async Task<(HttpClient Owner, Guid Ws, Guid Device, string Chat)> ConnectedProAsync(object? events = null, string channel = "tg")
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro");
        var (_, pair) = await factory.PairDeviceAsync(owner, ws);
        var chat = Unique("-100");
        await PutAsync(owner, ws, Settings("123:abc", chat, tgOn: true, events: events, channel: channel));
        return (owner, ws, pair.DeviceId, chat);
    }

    private static Task<HttpResponseMessage> CommandAsync(HttpClient client, Guid ws, Guid device, string cmd) =>
        client.PostAsJsonAsync($"/api/workspaces/{ws}/devices/{device}/commands", new { cmd }, Json);

    [Fact]
    public async Task Starting_and_stopping_posting_from_the_web_is_a_start_stop_notification()
    {
        var (owner, ws, device, chat) = await ConnectedProAsync();

        (await CommandAsync(owner, ws, device, "start")).EnsureSuccessStatusCode();
        (await CommandAsync(owner, ws, device, "stop")).EnsureSuccessStatusCode();

        var sent = factory.Notifications.To(chat);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, s => Assert.Equal(("tg", "123:abc"), (s.Channel, s.Token)));
        Assert.Contains("เริ่ม", sent[0].Text);
        Assert.Contains("Shop PC", sent[0].Text);
        Assert.Contains("หยุด", sent[1].Text);
    }

    [Fact]
    public async Task Other_buttons_and_switched_off_events_send_nothing()
    {
        var (owner, ws, device, chat) = await ConnectedProAsync();

        foreach (var cmd in new[] { "runNow", "testPost", "clearLogs", "syncNow" })
            (await CommandAsync(owner, ws, device, cmd)).EnsureSuccessStatusCode();
        Assert.Empty(factory.Notifications.To(chat));

        await PutAsync(owner, ws, Settings("123:abc", chat, tgOn: true, events: Events(startStop: false)));
        (await CommandAsync(owner, ws, device, "start")).EnsureSuccessStatusCode();
        Assert.Empty(factory.Notifications.To(chat));

        // A refused command (unknown name) sends nothing either.
        await AssertProblemAsync(await CommandAsync(owner, ws, device, "rm -rf"), HttpStatusCode.BadRequest);
        Assert.Empty(factory.Notifications.To(chat));
    }

    [Fact]
    public async Task A_failing_gateway_never_fails_the_command()
    {
        var (owner, ws, device, chat) = await ConnectedProAsync();
        factory.Notifications.TelegramResult = GatewayResult.Failure("ล้มเหลว");
        try
        {
            var res = await CommandAsync(owner, ws, device, "start");
            res.EnsureSuccessStatusCode();
            Assert.Equal(CommandStatus.Pending, (await res.Content.ReadFromJsonAsync<DeviceCommandDto>(Json))!.Status);
            Assert.Single(factory.Notifications.To(chat)); // it was tried
        }
        finally
        {
            factory.Notifications.TelegramResult = GatewayResult.Success;
        }
    }

    [Fact]
    public async Task Settings_that_outlive_a_downgrade_send_nothing()
    {
        var (owner, ws, device, chat) = await ConnectedProAsync();
        var admin = await factory.AdminAsync();
        var me = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;
        (await admin.PutAsJsonAsync($"/api/admin/customers/{me.Id}/plan", new { plan = "free" }, Json)).EnsureSuccessStatusCode();

        (await CommandAsync(owner, ws, device, "start")).EnsureSuccessStatusCode();

        Assert.Empty(factory.Notifications.To(chat));
        Assert.True((await GetAsync(owner, ws)).Telegram.HasToken); // the settings themselves stay
    }
}
