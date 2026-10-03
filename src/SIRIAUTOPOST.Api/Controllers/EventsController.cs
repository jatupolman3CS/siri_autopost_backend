using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Events;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>
/// The workspace's event stream (Server-Sent Events): what its devices do, as it happens, so the web app does
/// not poll. Every event carries its Seq as the SSE id; a client that lost the connection reconnects with
/// <c>after=&lt;last Seq&gt;</c> (or the Last-Event-ID header) and first gets everything it missed from the
/// database, then live events. The stream ends by itself after <see cref="MaxDuration"/> and when the API
/// shuts down (event "reconnect"); the client simply connects again.
/// </summary>
[ApiController]
[Route("api/workspaces/{wsId:guid}/events")]
public sealed class EventsController : ControllerBase
{
    /// <summary>A comment line this often keeps proxies and browsers from closing an idle stream.</summary>
    public static readonly TimeSpan Ping = TimeSpan.FromSeconds(15);
    /// <summary>A stream is closed cleanly after this long; the client reconnects from its last Seq.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The missed events (after <c>after</c>) as JSON, for clients that cannot stream.</summary>
    [HttpGet]
    [Produces("application/json")]
    public Task<DeviceEventsPageDto> List(
        Guid wsId, [FromServices] IQueryHandler<GetWorkspaceEventsQuery, DeviceEventsPageDto> handler, CancellationToken ct,
        [FromQuery] long after = 0, [FromQuery] int take = 200) =>
        handler.HandleAsync(new GetWorkspaceEventsQuery(wsId, after, take), ct);

    /// <summary>
    /// text/event-stream. Without <c>after</c> (or Last-Event-ID) the stream starts at the head: the first event is
    /// "ready" with the head Seq. Events: "id: Seq", "event: &lt;type&gt;", "data: DeviceEventDto".
    /// </summary>
    [HttpGet("stream")]
    [Produces("text/event-stream")]
    public async Task Stream(
        Guid wsId, [FromQuery] long? after,
        [FromServices] IQueryHandler<GetWorkspaceEventsQuery, DeviceEventsPageDto> handler,
        [FromServices] IDeviceEventBus bus, CancellationToken ct)
    {
        if (after is null && long.TryParse(Request.Headers["Last-Event-ID"], out var last)) after = last;

        // Subscribe before reading the backlog, so nothing saved in between is missed; Seq filtering drops repeats.
        using var sub = bus.Subscribe(wsId);
        // Also checks the caller may see the workspace (404/403 before any byte is streamed).
        var page = await handler.HandleAsync(new GetWorkspaceEventsQuery(wsId, after ?? long.MaxValue, GetWorkspaceEventsQueryHandler.MaxTake), ct);
        var sent = after ?? page.Head;

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no"; // nginx: do not buffer
        await Response.Body.FlushAsync(ct);

        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct, bus.Stopping);
        life.CancelAfter(MaxDuration);
        var token = life.Token;
        try
        {
            await WriteAsync($"retry: 3000\nevent: ready\ndata: {{\"head\":{page.Head}}}\n\n", token);
            if (after is not null)
            {
                foreach (var e in page.Events) await WriteEventAsync(e, token);
                if (page.Events.Count > 0) sent = Math.Max(sent, page.Events[^1].Seq);
                if (page.More)
                {
                    // Too far behind for one page: the client resumes from the last Seq it got.
                    await WriteAsync("event: reconnect\ndata: {\"reason\":\"backlog\"}\n\n", token);
                    return;
                }
            }

            while (!token.IsCancellationRequested)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(token);
                tick.CancelAfter(Ping);
                DeviceEvent? e;
                try
                {
                    if (!await sub.Events.WaitToReadAsync(tick.Token)) break; // bus stopped
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    await WriteAsync(": ping\n\n", token);
                    continue;
                }
                while (sub.Events.TryRead(out e))
                {
                    if (e.Seq <= sent) continue;
                    sent = e.Seq;
                    await WriteEventAsync(DeviceEventDto.From(e), token);
                }
            }
            if (!ct.IsCancellationRequested)
                await WriteAsync("event: reconnect\ndata: {\"reason\":\"" + (bus.Stopping.IsCancellationRequested ? "shutdown" : "rotate") + "\"}\n\n", ct);
        }
        catch (OperationCanceledException)
        {
            // the browser went away, or we are shutting down
        }
        catch (IOException)
        {
            // the connection dropped mid-write
        }
    }

    private Task WriteEventAsync(DeviceEventDto e, CancellationToken ct) =>
        WriteAsync($"id: {e.Seq}\nevent: {e.Type}\ndata: {JsonSerializer.Serialize(e, Json)}\n\n", ct);

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await Response.Body.FlushAsync(ct);
    }
}
