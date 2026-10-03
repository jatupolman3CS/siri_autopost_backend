using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The workspace's event stream: events are written with the changes they describe, can be read back by Seq,
// are pushed live over SSE, and wake a device waiting in a long sync.
[Collection(ApiCollection.Name)]
public class EventsEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = ApiFactory.Json;

    private sealed record Paired(HttpClient Owner, Guid Ws, HttpClient Device, PairResultDto Pair)
    {
        public string Base => $"/api/workspaces/{Ws}/devices/{Pair.DeviceId}";
        public string Events => $"/api/workspaces/{Ws}/events";
    }

    private async Task<Paired> PairAsync()
    {
        var (owner, _, ws) = await factory.SignUpAsync();
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content
            .ReadFromJsonAsync<PairingCodeDto>(Json))!;
        var device = factory.CreateClient();
        var pair = (await (await device.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "Shop PC" }, Json)).Content
            .ReadFromJsonAsync<PairResultDto>(Json))!;
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        return new Paired(owner, ws, device, pair);
    }

    private static Task<HttpResponseMessage> SyncAsync(HttpClient device, object body) =>
        device.PostAsJsonAsync("/api/device/sync", body, Json);

    [Fact]
    public async Task Events_are_written_with_the_changes_and_read_back_by_seq()
    {
        var p = await PairAsync();
        var first = (await p.Owner.GetFromJsonAsync<DeviceEventsPageDto>($"{p.Events}?after=0", Json))!;
        var paired = Assert.Single(first.Events, e => e.Type == DeviceEventType.Paired);
        Assert.Equal(p.Pair.DeviceId, paired.DeviceId);
        Assert.Equal("Shop PC", paired.Payload.GetProperty("name").GetString());
        Assert.Equal(first.Head, first.Events[^1].Seq);

        // State and log lines arrive as events with their content; a button becomes a command event.
        var sync = await SyncAsync(p.Device, new { version = "2.2.0", state = new { running = true }, logs = new[] { new { t = 1000, level = "info", msg = "เริ่มทำงาน" } }, takeCommands = false });
        sync.EnsureSuccessStatusCode();
        var cmd = (await (await p.Owner.PostAsJsonAsync($"{p.Base}/commands", new { cmd = "stop" }, Json)).Content.ReadFromJsonAsync<DeviceCommandDto>(Json))!;

        var page = (await p.Owner.GetFromJsonAsync<DeviceEventsPageDto>($"{p.Events}?after={first.Head}", Json))!;
        Assert.Equal([DeviceEventType.State, DeviceEventType.Log, DeviceEventType.Command], page.Events.Select(e => e.Type).ToArray());
        Assert.True(page.Events[0].Payload.GetProperty("state").GetProperty("running").GetBoolean());
        Assert.Equal("เริ่มทำงาน", page.Events[1].Payload.GetProperty("lines")[0].GetProperty("msg").GetString());
        Assert.False(page.Events[1].Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(cmd.Id, page.Events[2].Payload.GetProperty("id").GetGuid());
        Assert.Equal("pending", page.Events[2].Payload.GetProperty("status").GetString());
        Assert.True(page.Events.Zip(page.Events.Skip(1)).All(x => x.First.Seq < x.Second.Seq), "ordered by seq");
        Assert.False(page.More);

        // Paging: asking for one at a time says there is more.
        var one = (await p.Owner.GetFromJsonAsync<DeviceEventsPageDto>($"{p.Events}?after={first.Head}&take=1", Json))!;
        Assert.Single(one.Events);
        Assert.True(one.More);

        // Another user cannot read the workspace's events.
        var (other, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{p.Events}?after=0")).StatusCode);
    }

    [Fact]
    public async Task A_waiting_sync_returns_as_soon_as_the_web_app_sends_a_command()
    {
        var p = await PairAsync();
        var watch = Stopwatch.StartNew();
        var waiting = SyncAsync(p.Device, new { version = "2.2.0", takeCommands = true, wait = true });
        await Task.Delay(300); // let the sync reach its wait
        Assert.False(waiting.IsCompleted, "the sync holds while nothing is pending");

        var cmd = (await (await p.Owner.PostAsJsonAsync($"{p.Base}/commands", new { cmd = "start" }, Json)).Content.ReadFromJsonAsync<DeviceCommandDto>(Json))!;
        var res = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<DeviceSyncDto>(Json))!;
        Assert.Equal(cmd.Id, Assert.Single(dto.Commands).Id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");

        // The command is "sent" now, so a second waiting sync has nothing and simply gets nothing pending.
        var again = (await (await p.Owner.GetAsync($"{p.Base}/commands/{cmd.Id}")).Content.ReadFromJsonAsync<DeviceCommandDto>(Json))!;
        Assert.Equal(Domain.Enums.CommandStatus.Sent, again.Status);

        // A sync that is not asked to wait answers at once even with nothing pending.
        watch.Restart();
        (await SyncAsync(p.Device, new { version = "2.2.0", takeCommands = true })).EnsureSuccessStatusCode();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_stream_sends_the_backlog_then_live_events()
    {
        var p = await PairAsync();
        var head = (await p.Owner.GetFromJsonAsync<DeviceEventsPageDto>($"{p.Events}?after=0", Json))!.Head;
        // One event the stream has to catch up on.
        (await SyncAsync(p.Device, new { version = "2.2.0", state = new { running = false }, takeCommands = false })).EnsureSuccessStatusCode();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{p.Events}/stream?after={head}");
        using var res = await p.Owner.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType!.ToString());
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cts.Token));

        async Task<(string Id, string Event, string Data)> NextAsync()
        {
            string id = "", ev = "", data = "";
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
                if (line.Length == 0)
                {
                    if (ev.Length > 0) return (id, ev, data);
                    continue;
                }
                if (line.StartsWith(':')) continue; // ping
                if (line.StartsWith("id: ")) id = line[4..];
                else if (line.StartsWith("event: ")) ev = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
            }
        }

        var ready = await NextAsync();
        Assert.Equal("ready", ready.Event);
        var backlog = await NextAsync();
        Assert.Equal(DeviceEventType.State, backlog.Event);
        var backlogDto = JsonSerializer.Deserialize<DeviceEventDto>(backlog.Data, Json)!;
        Assert.Equal(backlogDto.Seq.ToString(), backlog.Id);
        Assert.True(backlogDto.Seq > head);

        // Live: a command sent now shows up on the open stream.
        var cmd = (await (await p.Owner.PostAsJsonAsync($"{p.Base}/commands", new { cmd = "stop" }, Json)).Content.ReadFromJsonAsync<DeviceCommandDto>(Json))!;
        var live = await NextAsync();
        Assert.Equal(DeviceEventType.Command, live.Event);
        var liveDto = JsonSerializer.Deserialize<DeviceEventDto>(live.Data, Json)!;
        Assert.Equal(cmd.Id, liveDto.Payload.GetProperty("id").GetGuid());
        Assert.True(liveDto.Seq > backlogDto.Seq);

        // The device answers: the page waiting for the command sees "done" on the stream.
        var taken = (await (await SyncAsync(p.Device, new { version = "2.2.0", takeCommands = true })).Content.ReadFromJsonAsync<DeviceSyncDto>(Json))!;
        Assert.Equal(cmd.Id, Assert.Single(taken.Commands).Id);
        (await p.Device.PostAsJsonAsync($"/api/device/commands/{cmd.Id}/result", new { result = new { ok = true } }, Json)).EnsureSuccessStatusCode();
        var done = await NextAsync();
        Assert.Equal(DeviceEventType.Command, done.Event);
        Assert.Equal("done", JsonSerializer.Deserialize<DeviceEventDto>(done.Data, Json)!.Payload.GetProperty("status").GetString());

        var health = (await (await factory.AdminAsync()).GetFromJsonAsync<PlatformHealthDto>("/api/admin/health", Json))!;
        Assert.True(health.EventStreams >= 1);
        Assert.True(health.EventsPublished >= 4);
    }
}
