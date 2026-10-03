using System.Text.Json;
using System.Text.RegularExpressions;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// Reads the extension's settings JSON on the server. Mirrors campaignImageIds in client/lib/shared.js and
/// hasContent in client/lib/backup.js (and Services/Helpers.cs of the legacy server): change them together.
/// </summary>
public static partial class ExtensionSettings
{
    /// <summary>A settings object as the extension stores it (version 2: a "campaigns" array).</summary>
    public static bool IsValid(JsonElement s) =>
        s.ValueKind == JsonValueKind.Object &&
        s.TryGetProperty("campaigns", out var c) && c.ValueKind == JsonValueKind.Array;

    private static IEnumerable<JsonElement> Items(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : [];

    private static IEnumerable<string> Strings(JsonElement obj, string prop) =>
        Items(obj, prop).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!);

    /// <summary>Every media id the campaigns use (lead media and each post's images).</summary>
    public static HashSet<string> ImageIds(string? json)
    {
        var ids = new HashSet<string>();
        if (string.IsNullOrEmpty(json)) return ids;
        using var doc = JsonDocument.Parse(json);
        foreach (var c in Items(doc.RootElement, "campaigns"))
        {
            ids.UnionWith(Strings(c, "leadImageIds"));
            foreach (var p in Items(c, "posts")) ids.UnionWith(Strings(p, "imageIds"));
        }
        return ids;
    }

    /// <summary>True when the settings hold real data, not just an empty default campaign.</summary>
    public static bool HasContent(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        using var doc = JsonDocument.Parse(json);
        foreach (var c in Items(doc.RootElement, "campaigns"))
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

    /// <summary>A stored JSON text as an element for a response (null when empty).</summary>
    public static JsonElement? Element(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [GeneratedRegex(@"^data:((?:image|video)/[a-z0-9.+-]+);base64,", RegexOptions.IgnoreCase)]
    private static partial Regex DataUrlRegex();

    /// <summary>"data:image/jpeg;base64,..." to (type, bytes); null when it is not an image or video data URL.</summary>
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
