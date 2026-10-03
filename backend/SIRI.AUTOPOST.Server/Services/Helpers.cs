using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SIRI.AUTOPOST.Server.Services;

public static partial class Keys
{
    // Device key shown once to the admin; only its hash is stored.
    public static string NewDeviceKey() =>
        "dk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex ImageIdRegex();

    public static bool ValidImageId(string id) => ImageIdRegex().IsMatch(id);
}

public record ImageDto(string Name, string Type, string Data);

public static partial class Media
{
    [GeneratedRegex(@"^data:((?:image|video)/[a-z0-9.+-]+);base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataUrlRegex();

    // "data:image/jpeg;base64,..." -> (type, bytes); null when not an image/video data URL.
    public static (string Type, byte[] Bytes)? ParseDataUrl(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        var m = DataUrlRegex().Match(data);
        if (!m.Success) return null;
        try
        {
            return (m.Groups[1].Value.ToLowerInvariant(), Convert.FromBase64String(data[m.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string ToDataUrl(string type, byte[] bytes) => $"data:{type};base64,{Convert.ToBase64String(bytes)}";
}

// Reads the extension's settings JSON (see lib/shared.js migrateSettings).
public static class SettingsJson
{
    public static bool IsValid(JsonElement s) =>
        s.ValueKind == JsonValueKind.Object &&
        s.TryGetProperty("campaigns", out var c) && c.ValueKind == JsonValueKind.Array;

    static IEnumerable<JsonElement> Campaigns(JsonElement s) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty("campaigns", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray()
            : [];

    static IEnumerable<string> Strings(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];

    static IEnumerable<JsonElement> Items(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : [];

    // Every media id the campaigns use (campaignImageIds in lib/shared.js).
    public static HashSet<string> ImageIds(string? json)
    {
        var ids = new HashSet<string>();
        if (string.IsNullOrEmpty(json)) return ids;
        using var doc = JsonDocument.Parse(json);
        foreach (var c in Campaigns(doc.RootElement))
        {
            ids.UnionWith(Strings(c, "leadImageIds"));
            foreach (var p in Items(c, "posts")) ids.UnionWith(Strings(p, "imageIds"));
        }
        return ids;
    }

    public record Summary(int Campaigns, int Groups, int Posts);

    public static Summary Summarize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new(0, 0, 0);
        using var doc = JsonDocument.Parse(json);
        int camps = 0, groups = 0, posts = 0;
        foreach (var c in Campaigns(doc.RootElement))
        {
            camps++;
            groups += Items(c, "groups").Count();
            posts += Items(c, "posts").Count();
        }
        return new(camps, groups, posts);
    }

    // True when the settings hold real data (hasContent in lib/backup.js).
    public static bool HasContent(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        using var doc = JsonDocument.Parse(json);
        foreach (var c in Campaigns(doc.RootElement))
        {
            if (Items(c, "groups").Any() || Strings(c, "leadImageIds").Any()) return true;
            foreach (var p in Items(c, "posts"))
            {
                var text = p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                if (!string.IsNullOrWhiteSpace(text) || Strings(p, "imageIds").Any()) return true;
            }
        }
        return false;
    }
}
