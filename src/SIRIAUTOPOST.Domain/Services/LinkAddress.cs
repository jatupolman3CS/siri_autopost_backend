using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>
/// Address helper that routes normalization, validation, and comparison to the platform-specific URL service.
/// </summary>
public static class LinkAddress
{
    public static string? Normalize(Platform channel, string? raw) =>
        channel == Platform.Line ? LineChatUrl.Normalize(raw) : FacebookGroupUrl.Normalize(raw);

    public static bool IsValid(Platform channel, string? raw) =>
        channel == Platform.Line ? LineChatUrl.IsValid(raw) : FacebookGroupUrl.IsValid(raw);

    public static StringComparer Comparer(Platform channel) =>
        channel == Platform.Line ? LineChatUrl.Comparer : FacebookGroupUrl.Comparer;

    public static bool Same(Platform channel, string? a, string? b) =>
        channel == Platform.Line ? LineChatUrl.Same(a, b) : FacebookGroupUrl.Same(a, b);
}
