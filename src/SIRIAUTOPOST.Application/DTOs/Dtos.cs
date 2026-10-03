using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.DTOs;

public sealed record UserDto(Guid Id, string Email, string Name, UserRole Role, PlanKey Plan, BillingCycle Cycle, CustomerStatus Status)
{
    public static UserDto From(User u) => new(u.Id, u.Email, u.Name, u.Role, u.Plan, u.Cycle, u.Status);
}

/// <param name="Price">Baht per month, monthly billing.</param>
/// <param name="Posts">Posts per 24 hours; null = unlimited (same for the other limits).</param>
public sealed record PlanDto(PlanKey Key, int Price, int? Accounts, int? Posts, int? Devices, int? Seats)
{
    public static PlanDto From(PlanSetting p) => new(p.Key, p.Price, p.Accounts, p.Posts, p.Devices, p.Seats);
}

public sealed record TransactionDto(
    Guid Id, Guid UserId, TransactionType Type, int Amount, PlanKey Plan, BillingCycle Cycle, string? PromoCode, DateTimeOffset CreatedAt)
{
    public static TransactionDto From(Transaction t) => new(t.Id, t.UserId, t.Type, t.Amount, t.Plan, t.Cycle, t.PromoCode, t.CreatedAt);
}

public sealed record AuthResultDto(string Token, DateTimeOffset ExpiresAt, UserDto User);

/// <param name="Role">The signed-in user's role here (owner for their own workspaces).</param>
/// <param name="Members">People with access, owner included.</param>
public sealed record WorkspaceDto(Guid Id, string Name, int Posts7, int Members, WorkspaceRole Role);

/// <param name="Connected">Posts through a paired browser (false for the demo accounts).</param>
public sealed record AccountDto(
    Guid Id, Platform Platform, string Name, string Handle, string DefaultTarget, AccountHealth Health,
    IReadOnlyList<string> Groups, bool Connected)
{
    public static AccountDto From(SocialAccount a) =>
        new(a.Id, a.Platform, a.Name, a.Handle, a.DefaultTarget, a.Health, a.Groups, a.IsConnected);
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
    string? FailureDetail,
    DateTimeOffset? PublishedAt)
{
    public static PostDto From(Post p) =>
        new(p.Id, p.AccountId, p.Platform, p.Target, p.Content, p.MediaIds, p.ScheduledAt, p.Status, p.FailureCode,
            p.FailureDetail, p.PublishedAt);
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

/// <param name="ExtensionOnline">
/// Off while the offline simulation holds it; otherwise on when no device is paired yet (demo) or when a
/// paired device called in within the last 100 seconds.
/// </param>
/// <param name="SimulatedOffline">The offline simulation is on (the web app's "simulate offline").</param>
/// <param name="Devices">Paired devices.</param>
/// <param name="DevicesOnline">Paired devices seen within the last 100 seconds.</param>
public sealed record EngineSettingsDto(
    AntiBanDto AntiBan, OfflineDto Offline, bool ExtensionOnline, bool SimulatedOffline, int Devices, int DevicesOnline)
{
    public static EngineSettingsDto From(Workspace ws, IReadOnlyList<Device> devices, DateTimeOffset now)
    {
        var online = devices.Count(d => d.IsOnline(now));
        return new(AntiBanDto.From(ws.AntiBan), OfflineDto.From(ws.Offline),
            ws.ExtensionOnline && (devices.Count == 0 || online > 0), !ws.ExtensionOnline, devices.Count, online);
    }
}

/// <summary>How many posts the offline simulation moved.</summary>
public sealed record ExtensionStateDto(bool Online, int Affected);

/// <param name="Online">Called in within the last 100 seconds.</param>
/// <param name="AccountId">The Facebook account this browser posts with.</param>
public sealed record DeviceDto(
    Guid Id, string Name, string Browser, string Version, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Online, Guid? AccountId)
{
    public static DeviceDto From(Device d, Guid? accountId, DateTimeOffset now) =>
        new(d.Id, d.Name, d.Browser, d.Version, d.CreatedAt, d.LastSeenAt, d.IsOnline(now), accountId);
}

/// <param name="MaxDevices">The owner's plan limit; null = unlimited.</param>
public sealed record PairingCodeDto(string Code, DateTimeOffset ExpiresAt, int? MaxDevices);

/// <summary>Returned once to the extension; only its hash is stored.</summary>
public sealed record PairResultDto(string DeviceKey, Guid DeviceId, string DeviceName, Guid WorkspaceId, string WorkspaceName, Guid AccountId);

/// <param name="Online">False while the offline simulation holds the workspace offline: take no jobs.</param>
public sealed record DeviceStatusDto(
    Guid DeviceId, string DeviceName, Guid WorkspaceId, string WorkspaceName, Guid? AccountId, int Groups, bool Online, AntiBanDto AntiBan);

public sealed record GroupLinkDto(string Name, string Url);

public sealed record JobMediaDto(Guid Id, string Name, string ContentType);

/// <summary>One post for the extension to publish now, in one Facebook group.</summary>
public sealed record JobDto(Guid PostId, string GroupName, string GroupUrl, string Content, IReadOnlyList<JobMediaDto> Media, AntiBanDto AntiBan);

/// <param name="Id">The membership; null for the owner.</param>
/// <param name="Active">Joined (false: invited, waiting for that email to sign up).</param>
public sealed record MemberDto(
    Guid? Id, Guid? UserId, string Email, string Name, WorkspaceRole Role, bool Active, DateTimeOffset? LastSeenAt, bool You);

public sealed record CustomerJobsDto(int Ok, int Failed, int Queued, int Running);

public sealed record CustomerDeviceDto(Guid Id, string Name, string Browser, DateTimeOffset? LastSeenAt, bool Online);

/// <summary>null keeps the plan's value, 0 = unlimited.</summary>
public sealed record LimitOverridesDto(int? Accounts, int? Posts, int? Devices, int? Seats);

/// <param name="Accounts">Accounts connected through the extension.</param>
/// <param name="Seats">People in the customer's workspaces, the customer included.</param>
/// <param name="Ext">Extension version of the most recently seen device.</param>
/// <param name="Jobs">Posts of the last 24 hours (ok, failed) and the queue of the next 30 days.</param>
public sealed record CustomerDto(
    Guid Id, string Name, string Email, PlanKey Plan, CustomerStatus Status, DateTimeOffset Since, BillingCycle Cycle,
    int Accounts, int Seats, string Ext, DateTimeOffset? LastActiveAt, bool Paused, CustomerJobsDto Jobs,
    IReadOnlyList<CustomerDeviceDto> Devices, string? Note, int Workspaces, LimitOverridesDto Limits);

public sealed record RevenueMonthDto(int Year, int Month, int Amount);

/// <param name="Basic">Paying customers per plan (active or past due).</param>
/// <param name="Revenue">Charges minus refunds, the last 12 months, oldest first.</param>
/// <summary>
/// The admin overview's live figures. Changes compare with 30 days earlier (MRR, churn) or the 7 days
/// before (success rate). Rates are percentages; null when there is nothing to measure yet.
/// </summary>
public sealed record PlatformHealthDto(
    int Mrr, int MrrPrev,
    double Churn, double ChurnPrev,
    int DevicesActive, int Devices,
    double? SuccessRate, double? SuccessRatePrev,
    int? ApiP95Ms, int ApiSamples, int DbMs,
    int QueueDue, int QueueNext24h,
    string? LatestExtension, int OnLatestExtension,
    double? ErrorRate24h,
    bool PaymentsConnected);

/// <summary>One activity-log line. From/To are values (plan keys, statuses, amounts), not display text.</summary>
public sealed record AuditEntryDto(
    Guid Id, DateTimeOffset At, AuditAction Action, Guid ActorId, string ActorEmail, Guid? CustomerId, string? CustomerEmail,
    string? From, string? To);

public sealed record AdminSummaryDto(int Basic, int Pro, int Agency, IReadOnlyList<RevenueMonthDto> Revenue);

public sealed record AdminJobDto(
    Guid PostId, Guid CustomerId, string Customer, Platform Platform, string Target, string Content, DateTimeOffset ScheduledAt,
    PostStatus Status, FailureCode? FailureCode);

public sealed record PromoDto(string Code, string Discount, int Uses, DateTimeOffset ExpiresAt, bool Active)
{
    public static PromoDto From(Promo p) => new(p.Code, p.Discount, p.Uses, p.ExpiresAt, p.Active);
}
