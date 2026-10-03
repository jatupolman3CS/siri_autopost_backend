using System.Text.Json;
using System.Text.Json.Nodes;
using AutoPost.Server.Data;
using AutoPost.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AutoPost.Server.Endpoints;

// API for the extension (header X-Device-Key). CORS is open on these routes
// because the extension calls them from its service worker.
public static class DeviceEndpoints
{
    public const string CorsPolicy = "device";
    const int MaxLogsPerDevice = 1000;
    static readonly TimeSpan CommandTtl = TimeSpan.FromMinutes(10);

    public record LogEntry(long T, string? Level, string? Msg);
    public record HeartbeatRequest(string? Version, JsonElement? State, List<LogEntry>? Logs, bool? TakeCommands);
    public record SettingsRequest(JsonElement Settings, long? BaseRevision);
    public record IdsRequest(List<string>? Ids);
    public record ResultRequest(JsonElement? Result);

    static async Task<Device?> Auth(HttpContext ctx, AppDb db)
    {
        var key = ctx.Request.Headers["X-Device-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key)) return null;
        var hash = Keys.Hash(key);
        return await db.Devices.Include(d => d.Profile).FirstOrDefaultAsync(d => d.KeyHash == hash);
    }

    static IResult Denied() => Results.Json(new { error = "คีย์เครื่องไม่ถูกต้อง หรือเครื่องถูกลบจาก server แล้ว" }, statusCode: 401);
    static IResult NoProfile() => Results.Json(new { error = "เครื่องนี้ยังไม่ได้เลือก config บน server" }, statusCode: 409);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/device").RequireCors(CorsPolicy);

        // Reports state/logs, returns the config revision and pending commands.
        g.MapPost("/heartbeat", async (HeartbeatRequest req, HttpContext ctx, AppDb db) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            var now = DateTime.UtcNow;
            d.LastSeenAt = now;
            d.Version = (req.Version ?? "")[..Math.Min((req.Version ?? "").Length, 50)];
            if (req.State is JsonElement st && st.ValueKind == JsonValueKind.Object)
            {
                d.State = st.GetRawText();
                d.StateAt = now;
            }
            if (req.Logs is { Count: > 0 } logs)
            {
                foreach (var l in logs.TakeLast(MaxLogsPerDevice))
                {
                    db.DeviceLogs.Add(new DeviceLog
                    {
                        DeviceId = d.Id,
                        T = l.T,
                        Level = (l.Level ?? "info")[..Math.Min((l.Level ?? "info").Length, 20)],
                        Msg = l.Msg ?? "",
                    });
                }
            }
            await db.SaveChangesAsync();
            if (req.Logs is { Count: > 0 })
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM fbap_device_logs WHERE device_id = {d.Id} AND id < (
                      SELECT id FROM fbap_device_logs WHERE device_id = {d.Id}
                      ORDER BY id DESC OFFSET {MaxLogsPerDevice - 1} LIMIT 1)
                    """);
            }

            var commands = new JsonArray();
            if (req.TakeCommands != false)
            {
                // Old commands are dropped: a "start" from yesterday must not run now.
                var stale = now - CommandTtl;
                await db.DeviceCommands
                    .Where(c => c.DeviceId == d.Id && c.Status == CommandStatus.Pending && c.CreatedAt < stale)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.Expired));
                var pending = await db.DeviceCommands
                    .Where(c => c.DeviceId == d.Id && c.Status == CommandStatus.Pending)
                    .OrderBy(c => c.Id)
                    .ToListAsync();
                foreach (var c in pending)
                {
                    c.Status = CommandStatus.Sent;
                    c.SentAt = now;
                    commands.Add(new JsonObject { ["id"] = c.Id, ["cmd"] = c.Cmd, ["args"] = JsonNode.Parse(c.Args) });
                }
                await db.SaveChangesAsync();
            }

            return Results.Ok(new JsonObject
            {
                ["device"] = new JsonObject { ["id"] = d.Id, ["name"] = d.Name },
                ["profile"] = d.Profile is null ? null : new JsonObject
                {
                    ["id"] = d.Profile.Id,
                    ["name"] = d.Profile.Name,
                    ["revision"] = d.Profile.Revision,
                    ["hasContent"] = SettingsJson.HasContent(d.Profile.Settings),
                },
                ["commands"] = commands,
                ["serverTime"] = now,
            });
        });

        g.MapGet("/config", async (HttpContext ctx, AppDb db) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            return d.Profile is null ? NoProfile() : Results.Ok(ProfileStore.SettingsResult(d.Profile));
        });

        // Local edits made on the device itself.
        g.MapPut("/config", async (SettingsRequest req, HttpContext ctx, AppDb db, ProfileStore store) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            if (d.ProfileId is not Guid pid) return NoProfile();
            var r = await store.SaveSettings(pid, req.Settings, req.BaseRevision, "device:" + d.Name);
            if (r.Conflict) return Results.Json(new { error = r.Error, revision = r.Revision }, statusCode: 409);
            return r.Ok ? Results.Ok(new { revision = r.Revision }) : Results.Json(new { error = r.Error }, statusCode: 400);
        });

        g.MapPost("/images/missing", async (IdsRequest req, HttpContext ctx, AppDb db, ProfileStore store) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            if (d.ProfileId is not Guid pid) return NoProfile();
            return Results.Ok(new { missing = await store.MissingImages(pid, req.Ids ?? []) });
        });

        g.MapGet("/images/{imageId}", async (string imageId, HttpContext ctx, AppDb db, ProfileStore store) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            if (d.ProfileId is not Guid pid) return NoProfile();
            var found = await store.GetImages(pid, [imageId]);
            return found.TryGetValue(imageId, out var img) ? Results.Ok(img) : Results.NotFound(new { error = "ไม่พบรูป" });
        });

        g.MapPut("/images/{imageId}", async (string imageId, ImageDto dto, HttpContext ctx, AppDb db, ProfileStore store) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            if (d.ProfileId is not Guid pid) return NoProfile();
            var err = await store.PutImage(pid, imageId, dto);
            return err is null ? Results.Ok(new { ok = true }) : Results.Json(new { error = err }, statusCode: 400);
        });

        g.MapPost("/commands/{id:long}/result", async (long id, ResultRequest req, HttpContext ctx, AppDb db) =>
        {
            var d = await Auth(ctx, db);
            if (d is null) return Denied();
            var c = await db.DeviceCommands.FirstOrDefaultAsync(x => x.Id == id && x.DeviceId == d.Id);
            if (c is null) return Results.NotFound();
            c.Status = CommandStatus.Done;
            c.DoneAt = DateTime.UtcNow;
            c.Result = req.Result is JsonElement r && r.ValueKind != JsonValueKind.Undefined ? r.GetRawText() : null;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });
    }
}
