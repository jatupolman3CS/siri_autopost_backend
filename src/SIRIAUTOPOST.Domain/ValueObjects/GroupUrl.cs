using System.Text.RegularExpressions;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

// A Facebook group link in one canonical form: https://www.facebook.com/groups/<id>/
// (same rule as normalizeGroupUrl in the extension's lib/shared.js).
public sealed partial record GroupUrl
{
    public string Value { get; }

    private GroupUrl(string value) => Value = value;

    public static GroupUrl Create(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) throw new DomainException("กรุณาใส่ลิงก์กลุ่ม");
        if (!SchemeRegex().IsMatch(s)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || !HostRegex().IsMatch(uri.Host))
            throw new DomainException("ลิงก์กลุ่มต้องเป็นลิงก์ facebook.com");
        var m = PathRegex().Match(uri.AbsolutePath);
        if (!m.Success) throw new DomainException("ลิงก์กลุ่มต้องอยู่ในรูปแบบ facebook.com/groups/...");
        return new GroupUrl($"https://www.facebook.com/groups/{m.Groups[1].Value}/");
    }

    public override string ToString() => Value;

    [GeneratedRegex("^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"(^|\.)facebook\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex HostRegex();

    [GeneratedRegex("^/groups/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();
}
