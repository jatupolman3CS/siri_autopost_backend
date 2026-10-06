namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// The picture of the posting window a device may send with a post's result, for the notification. It arrives as a data
/// URL (<c>data:image/jpeg;base64,...</c>) or bare base64; anything that is not a JPEG or PNG of a sensible size is
/// ignored (the result itself is what matters, never the picture).
/// </summary>
public static class ShotImage
{
    /// <summary>The largest picture kept. A screenshot of the window at JPEG quality 70 is about 100-300 KB.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public static byte[]? Parse(string? dataUrlOrBase64)
    {
        if (string.IsNullOrWhiteSpace(dataUrlOrBase64)) return null;
        var text = dataUrlOrBase64.AsSpan().Trim();
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma < 0) return null;
            text = text[(comma + 1)..];
        }
        // Base64 is 4 characters for 3 bytes: refuse a long one before decoding it.
        if (text.Length > MaxBytes / 3 * 4 + 8) return null;
        var buffer = new byte[text.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(text, buffer, out var written) || written == 0 || written > MaxBytes) return null;
        var bytes = buffer.AsSpan(0, written).ToArray();
        return IsJpeg(bytes) || IsPng(bytes) ? bytes : null;
    }

    public static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    public static bool IsPng(ReadOnlySpan<byte> b) => b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
}
