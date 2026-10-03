using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.DTOs;

public sealed record UserDto(Guid Id, string Email, string Name, UserRole Role, PlanKey Plan)
{
    public static UserDto From(User u) => new(u.Id, u.Email, u.Name, u.Role, u.Plan);
}

public sealed record AuthResultDto(string Token, DateTimeOffset ExpiresAt, UserDto User);

public sealed record WorkspaceDto(Guid Id, string Name, int Posts7, int Members);

public sealed record AccountDto(
    Guid Id, Platform Platform, string Name, string Handle, string DefaultTarget, AccountHealth Health, IReadOnlyList<string> Groups)
{
    public static AccountDto From(SocialAccount a) =>
        new(a.Id, a.Platform, a.Name, a.Handle, a.DefaultTarget, a.Health, a.Groups);
}

public sealed record PostDto(
    Guid Id,
    Guid AccountId,
    Platform Platform,
    string Target,
    string Content,
    IReadOnlyList<Guid> MediaIds,
    DateTimeOffset ScheduledAt,
    PostStatus Status,
    FailureCode? FailureCode,
    DateTimeOffset? PublishedAt)
{
    public static PostDto From(Post p) =>
        new(p.Id, p.AccountId, p.Platform, p.Target, p.Content, p.MediaIds, p.ScheduledAt, p.Status, p.FailureCode, p.PublishedAt);
}

public sealed record ScheduleResultDto(int Created, DateTimeOffset FirstAt, DateTimeOffset LastAt);

public sealed record MediaDto(Guid Id, string Name, string ContentType, MediaKind Kind, long Size, int UsedCount, DateTimeOffset CreatedAt)
{
    public static MediaDto From(MediaSummary m) => new(m.Id, m.Name, m.ContentType, m.Kind, m.Size, m.UsedCount, m.CreatedAt);

    public static MediaDto From(MediaFile m) => new(m.Id, m.Name, m.ContentType, m.Kind, m.Size, m.UsedCount, m.CreatedAt);
}

public sealed record MediaContent(string Name, string ContentType, byte[] Data);

public sealed record SnippetDto(Guid Id, string Title, string Text, int UsedCount)
{
    public static SnippetDto From(Snippet s) => new(s.Id, s.Title, s.Text, s.UsedCount);
}

public sealed record PlatformLimitsDto(int Fb, int X, int Ig, int Tt, int Line, int Th)
{
    public static PlatformLimitsDto From(PlatformLimits l) => new(l.Fb, l.X, l.Ig, l.Tt, l.Line, l.Th);

    public PlatformLimits ToSettings() => new() { Fb = Fb, X = X, Ig = Ig, Tt = Tt, Line = Line, Th = Th };
}

public sealed record AntiBanDto(int Min, int Max, PlatformLimitsDto Limits, bool Typing, bool Scroll, bool Shuffle, bool AutoPause, bool Warmup)
{
    public static AntiBanDto From(AntiBanSettings s) =>
        new(s.Min, s.Max, PlatformLimitsDto.From(s.Limits), s.Typing, s.Scroll, s.Shuffle, s.AutoPause, s.Warmup);

    public AntiBanSettings ToSettings() => new()
    {
        Min = Min, Max = Max, Limits = Limits.ToSettings(),
        Typing = Typing, Scroll = Scroll, Shuffle = Shuffle, AutoPause = AutoPause, Warmup = Warmup,
    };
}

public sealed record OfflineDto(OfflinePolicy Policy, string Window, bool Line, bool Email, bool Push)
{
    public static OfflineDto From(OfflineSettings s) => new(s.Policy, s.Window, s.Line, s.Email, s.Push);

    public OfflineSettings ToSettings() => new() { Policy = Policy, Window = Window, Line = Line, Email = Email, Push = Push };
}

public sealed record EngineSettingsDto(AntiBanDto AntiBan, OfflineDto Offline, bool ExtensionOnline)
{
    public static EngineSettingsDto From(Workspace ws) =>
        new(AntiBanDto.From(ws.AntiBan), OfflineDto.From(ws.Offline), ws.ExtensionOnline);
}

/// <summary>How many posts the offline simulation moved.</summary>
public sealed record ExtensionStateDto(bool Online, int Affected);
