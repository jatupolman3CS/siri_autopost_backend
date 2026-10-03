using System.Text.Json;
using System.Text.Json.Nodes;
using AutoPost.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPost.Server.Services;

// Settings and images of a profile; used by the admin web and the devices.
public class ProfileStore(AppDb db)
{
    // Images no settings use are deleted after this grace time (an editor
    // uploads images before it saves the settings that use them).
    static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);

    public static JsonObject SettingsResult(Profile p) => new()
    {
        ["profileId"] = p.Id,
        ["name"] = p.Name,
        ["revision"] = p.Revision,
        ["updatedAt"] = p.UpdatedAt,
        ["updatedBy"] = p.UpdatedBy,
        ["settings"] = p.Settings is null ? null : JsonNode.Parse(p.Settings),
    };

    public record SaveResult(bool Ok, long Revision, string? Error = null, bool Conflict = false);

    // baseRevision: the revision the editor started from; null = overwrite.
    public async Task<SaveResult> SaveSettings(Guid profileId, JsonElement settings, long? baseRevision, string by)
    {
        if (!SettingsJson.IsValid(settings)) return new(false, 0, "settings ไม่ถูกต้อง (ต้องมี campaigns)");
        var json = settings.GetRawText();
        // Same settings again (e.g. "save" before Start): keep the revision so
        // devices do not reload for nothing.
        var same = await db.Database
            .SqlQuery<bool>($"SELECT settings IS NOT DISTINCT FROM CAST({json} AS jsonb) AS \"Value\" FROM fbap_profiles WHERE id = {profileId}")
            .FirstOrDefaultAsync();
        if (same)
        {
            var rev = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => p.Revision).FirstAsync();
            if (baseRevision is null || baseRevision == rev) return new(true, rev);
        }
        var now = DateTime.UtcNow;
        var q = db.Profiles.Where(p => p.Id == profileId);
        if (baseRevision is long b) q = q.Where(p => p.Revision == b);
        var n = await q.ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Settings, json)
            .SetProperty(p => p.Revision, p => p.Revision + 1)
            .SetProperty(p => p.UpdatedAt, now)
            .SetProperty(p => p.UpdatedBy, by));
        var current = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => (long?)p.Revision).FirstOrDefaultAsync();
        if (current is null) return new(false, 0, "ไม่พบ config");
        if (n == 0) return new(false, current.Value, "config ถูกแก้จากที่อื่นแล้ว", Conflict: true);
        await DeleteOrphanImages(profileId, json);
        return new(true, current.Value);
    }

    async Task DeleteOrphanImages(Guid profileId, string json)
    {
        var used = SettingsJson.ImageIds(json);
        var before = DateTime.UtcNow - OrphanGrace;
        var old = await db.ProfileImages
            .Where(i => i.ProfileId == profileId && i.CreatedAt < before)
            .Select(i => i.ImageId)
            .ToListAsync();
        var orphan = old.Where(id => !used.Contains(id)).ToList();
        if (orphan.Count > 0)
            await db.ProfileImages.Where(i => i.ProfileId == profileId && orphan.Contains(i.ImageId)).ExecuteDeleteAsync();
    }

    public Task<List<string>> ImageIds(Guid profileId) =>
        db.ProfileImages.Where(i => i.ProfileId == profileId).Select(i => i.ImageId).ToListAsync();

    public async Task<List<string>> MissingImages(Guid profileId, IEnumerable<string> ids)
    {
        var want = ids.Where(Keys.ValidImageId).Distinct().ToList();
        var have = await db.ProfileImages
            .Where(i => i.ProfileId == profileId && want.Contains(i.ImageId))
            .Select(i => i.ImageId)
            .ToListAsync();
        return want.Except(have).ToList();
    }

    public async Task<Dictionary<string, ImageDto>> GetImages(Guid profileId, IEnumerable<string> ids)
    {
        var want = ids.Where(Keys.ValidImageId).Distinct().ToList();
        var rows = await db.ProfileImages.AsNoTracking()
            .Where(i => i.ProfileId == profileId && want.Contains(i.ImageId))
            .ToListAsync();
        return rows.ToDictionary(r => r.ImageId, r => new ImageDto(r.Name, r.ContentType, Media.ToDataUrl(r.ContentType, r.Data)));
    }

    // Image ids never change content in the extension, so an existing id is kept.
    public async Task<string?> PutImage(Guid profileId, string imageId, ImageDto? dto)
    {
        if (!Keys.ValidImageId(imageId)) return "id รูปไม่ถูกต้อง";
        var parsed = Media.ParseDataUrl(dto?.Data);
        if (dto is null || parsed is null) return "ข้อมูลรูปไม่ถูกต้อง";
        var row = await db.ProfileImages.FindAsync(profileId, imageId);
        if (row is null)
        {
            row = new ProfileImage { ProfileId = profileId, ImageId = imageId };
            db.ProfileImages.Add(row);
        }
        row.Name = string.IsNullOrWhiteSpace(dto.Name) ? "image" : dto.Name[..Math.Min(dto.Name.Length, 300)];
        row.ContentType = parsed.Value.Type;
        row.Data = parsed.Value.Bytes;
        row.Size = parsed.Value.Bytes.LongLength;
        row.CreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return null;
    }

    public Task DeleteImages(Guid profileId, IEnumerable<string> ids)
    {
        var list = ids.ToList();
        return db.ProfileImages.Where(i => i.ProfileId == profileId && list.Contains(i.ImageId)).ExecuteDeleteAsync();
    }
}
