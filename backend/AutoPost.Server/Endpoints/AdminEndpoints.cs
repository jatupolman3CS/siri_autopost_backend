using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoPost.Server.Data;
using AutoPost.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AutoPost.Server.Endpoints;

// Admin web API (cookie login): configs (profiles), devices, remote commands.
public static class AdminEndpoints
{
    public record NameRequest(string? Name, Guid? CopyFrom);
    public record SettingsRequest(JsonElement Settings, long? BaseRevision);
    public record IdsRequest(List<string>? Ids);
    public record DeviceRequest(string? Name, Guid? ProfileId);
    public record CommandRequest(string? Cmd, JsonElement? Args);

    // Commands the extension accepts (see background.js remoteCommands).
    static readonly HashSet<string> AllowedCommands =
        ["start", "stop", "runNow", "testPost", "tgTest", "tgFindChats", "clearLogs", "syncNow"];

    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(100);

    static string Who(ClaimsPrincipal u) => "web:" + u.Identity!.Name;

    static object DeviceView(Device d) => new
    {
        id = d.Id,
        name = d.Name,
        profileId = d.ProfileId,
        profileName = d.Profile?.Name,
        lastSeenAt = d.LastSeenAt,
        online = d.LastSeenAt > DateTime.UtcNow - OnlineWindow,
        version = d.Version,
        running = Running(d.State),
        createdAt = d.CreatedAt,
    };

    static bool Running(string? state)
    {
        if (state is null) return false;
        using var doc = JsonDocument.Parse(state);
        return doc.RootElement.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---------- profiles (configs) ----------

        g.MapGet("/profiles", async (AppDb db) =>
        {
            var rows = await db.Profiles.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
            var imgCounts = await db.ProfileImages.GroupBy(i => i.ProfileId)
                .Select(x => new { x.Key, Count = x.Count(), Size = x.Sum(i => i.Size) })
                .ToDictionaryAsync(x => x.Key);
            var devCounts = await db.Devices.Where(d => d.ProfileId != null).GroupBy(d => d.ProfileId!.Value)
                .Select(x => new { x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            return rows.Select(p =>
            {
                var s = SettingsJson.Summarize(p.Settings);
                imgCounts.TryGetValue(p.Id, out var img);
                return new
                {
                    id = p.Id,
                    name = p.Name,
                    revision = p.Revision,
                    updatedAt = p.UpdatedAt,
                    updatedBy = p.UpdatedBy,
                    campaigns = s.Campaigns,
                    groups = s.Groups,
                    posts = s.Posts,
                    images = img?.Count ?? 0,
                    imageBytes = img?.Size ?? 0,
                    devices = devCounts.GetValueOrDefault(p.Id),
                };
            });
        });

        g.MapPost("/profiles", async (NameRequest req, AppDb db, ClaimsPrincipal u) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "ใส่ชื่อ config" }, statusCode: 400);
            var p = new Profile { Name = name[..Math.Min(name.Length, 200)], UpdatedBy = Who(u) };
            if (req.CopyFrom is Guid src)
            {
                var from = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == src);
                if (from is null) return Results.NotFound(new { error = "ไม่พบ config ต้นทาง" });
                p.Settings = from.Settings;
                p.Revision = 1;
            }
            db.Profiles.Add(p);
            await db.SaveChangesAsync();
            if (req.CopyFrom is Guid srcId)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO fbap_profile_images (profile_id, image_id, name, content_type, data, size, created_at)
                    SELECT {p.Id}, image_id, name, content_type, data, size, now()
                    FROM fbap_profile_images WHERE profile_id = {srcId}
                    """);
            }
            return Results.Ok(new { id = p.Id });
        });

        g.MapPut("/profiles/{id:guid}", async (Guid id, NameRequest req, AppDb db) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "ใส่ชื่อ config" }, statusCode: 400);
            var n = await db.Profiles.Where(p => p.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, name[..Math.Min(name.Length, 200)]));
            return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
        });

        g.MapDelete("/profiles/{id:guid}", async (Guid id, AppDb db) =>
        {
            var n = await db.Profiles.Where(p => p.Id == id).ExecuteDeleteAsync();
            return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
        });

        g.MapGet("/profiles/{id:guid}/settings", async (Guid id, AppDb db) =>
        {
            var p = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return p is null ? Results.NotFound(new { error = "ไม่พบ config" }) : Results.Ok(ProfileStore.SettingsResult(p));
        });

        g.MapGet("/profiles/{id:guid}/revision", async (Guid id, AppDb db) =>
        {
            var p = await db.Profiles.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { revision = x.Revision, updatedAt = x.UpdatedAt, updatedBy = x.UpdatedBy })
                .FirstOrDefaultAsync();
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        g.MapPut("/profiles/{id:guid}/settings", async (Guid id, SettingsRequest req, ProfileStore store, ClaimsPrincipal u) =>
        {
            var r = await store.SaveSettings(id, req.Settings, req.BaseRevision, Who(u));
            if (r.Conflict) return Results.Json(new { error = r.Error, revision = r.Revision }, statusCode: 409);
            return r.Ok ? Results.Ok(new { revision = r.Revision }) : Results.Json(new { error = r.Error }, statusCode: 400);
        });

        g.MapGet("/profiles/{id:guid}/images", (Guid id, ProfileStore store) => store.ImageIds(id));

        g.MapPost("/profiles/{id:guid}/images/get", (Guid id, IdsRequest req, ProfileStore store) =>
            store.GetImages(id, req.Ids ?? []));

        g.MapPut("/profiles/{id:guid}/images/{imageId}", async (Guid id, string imageId, ImageDto dto, ProfileStore store, AppDb db) =>
        {
            if (!await db.Profiles.AnyAsync(p => p.Id == id)) return Results.NotFound(new { error = "ไม่พบ config" });
            var err = await store.PutImage(id, imageId, dto);
            return err is null ? Results.Ok(new { ok = true }) : Results.Json(new { error = err }, statusCode: 400);
        });

        g.MapPost("/profiles/{id:guid}/images/delete", async (Guid id, IdsRequest req, ProfileStore store) =>
        {
            await store.DeleteImages(id, req.Ids ?? []);
            return Results.Ok(new { ok = true });
        });

        // ---------- devices ----------

        g.MapGet("/devices", async (AppDb db) =>
        {
            var rows = await db.Devices.AsNoTracking().Include(d => d.Profile).OrderBy(d => d.CreatedAt).ToListAsync();
            return rows.Select(DeviceView);
        });

        g.MapPost("/devices", async (DeviceRequest req, AppDb db) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.Json(new { error = "ใส่ชื่อเครื่อง" }, statusCode: 400);
            if (req.ProfileId is Guid pid && !await db.Profiles.AnyAsync(p => p.Id == pid))
                return Results.Json(new { error = "ไม่พบ config" }, statusCode: 400);
            var key = Keys.NewDeviceKey();
            var d = new Device { Name = name[..Math.Min(name.Length, 200)], ProfileId = req.ProfileId, KeyHash = Keys.Hash(key) };
            db.Devices.Add(d);
            await db.SaveChangesAsync();
            return Results.Ok(new { id = d.Id, key });
        });

        g.MapPut("/devices/{id:guid}", async (Guid id, DeviceRequest req, AppDb db) =>
        {
            var d = await db.Devices.FindAsync(id);
            if (d is null) return Results.NotFound();
            var name = (req.Name ?? "").Trim();
            if (name.Length > 0) d.Name = name[..Math.Min(name.Length, 200)];
            if (req.ProfileId is Guid pid && !await db.Profiles.AnyAsync(p => p.Id == pid))
                return Results.Json(new { error = "ไม่พบ config" }, statusCode: 400);
            d.ProfileId = req.ProfileId;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/devices/{id:guid}/key", async (Guid id, AppDb db) =>
        {
            var d = await db.Devices.FindAsync(id);
            if (d is null) return Results.NotFound();
            var key = Keys.NewDeviceKey();
            d.KeyHash = Keys.Hash(key);
            await db.SaveChangesAsync();
            return Results.Ok(new { key });
        });

        g.MapDelete("/devices/{id:guid}", async (Guid id, AppDb db) =>
        {
            var n = await db.Devices.Where(d => d.Id == id).ExecuteDeleteAsync();
            return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
        });

        // State + new logs of a device, polled by the web dashboard.
        g.MapGet("/devices/{id:guid}/live", async (Guid id, long? afterLog, AppDb db) =>
        {
            var d = await db.Devices.AsNoTracking().Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound(new { error = "ไม่พบเครื่อง" });
            var q = db.DeviceLogs.AsNoTracking().Where(l => l.DeviceId == id);
            List<DeviceLog> logs = afterLog is long a && a > 0
                ? await q.Where(l => l.Id > a).OrderBy(l => l.Id).Take(500).ToListAsync()
                : (await q.OrderByDescending(l => l.Id).Take(400).ToListAsync()).AsEnumerable().Reverse().ToList();
            var res = new JsonObject
            {
                ["device"] = JsonSerializer.SerializeToNode(DeviceView(d)),
                ["state"] = d.State is null ? null : JsonNode.Parse(d.State),
                ["stateAt"] = d.StateAt,
                ["profileRevision"] = d.Profile?.Revision,
                ["logs"] = JsonSerializer.SerializeToNode(logs.Select(l => new { id = l.Id, t = l.T, level = l.Level, msg = l.Msg })),
            };
            return Results.Ok(res);
        });

        g.MapDelete("/devices/{id:guid}/logs", async (Guid id, AppDb db) =>
        {
            await db.DeviceLogs.Where(l => l.DeviceId == id).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        });

        // ---------- remote commands ----------

        g.MapPost("/devices/{id:guid}/commands", async (Guid id, CommandRequest req, AppDb db) =>
        {
            var cmd = req.Cmd ?? "";
            if (!AllowedCommands.Contains(cmd)) return Results.Json(new { error = $"ไม่รู้จักคำสั่ง {cmd}" }, statusCode: 400);
            if (!await db.Devices.AnyAsync(d => d.Id == id)) return Results.NotFound(new { error = "ไม่พบเครื่อง" });
            var c = new DeviceCommand
            {
                DeviceId = id,
                Cmd = cmd,
                Args = req.Args is JsonElement a && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : "{}",
            };
            db.DeviceCommands.Add(c);
            await db.SaveChangesAsync();
            return Results.Ok(new { id = c.Id });
        });

        g.MapGet("/commands/{id:long}", async (long id, AppDb db) =>
        {
            var c = await db.DeviceCommands.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            return Results.Ok(new JsonObject
            {
                ["id"] = c.Id,
                ["cmd"] = c.Cmd,
                ["status"] = c.Status,
                ["result"] = c.Result is null ? null : JsonNode.Parse(c.Result),
            });
        });
    }
}
