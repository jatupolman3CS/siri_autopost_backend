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

/// <param name="Amount">Baht, positive (a refund is money going back); Stripe's satang give it cents.</param>
/// <param name="ReceiptUrl">Stripe's hosted invoice page; null for rows without a Stripe invoice.</param>
/// <param name="Refundable">The platform admin can refund it through Stripe (a charge paid there; refunds already made are not counted here).</param>
/// <param name="RefundOfId">On a refund: the charge it gives money back for.</param>
public sealed record TransactionDto(
    Guid Id, Guid UserId, TransactionType Type, decimal Amount, PlanKey Plan, BillingCycle Cycle, string? PromoCode, DateTimeOffset CreatedAt,
    string? ReceiptUrl, bool Refundable, Guid? RefundOfId)
{
    public static TransactionDto From(Transaction t) =>
        new(t.Id, t.UserId, t.Type, t.Amount, t.Plan, t.Cycle, t.PromoCode, t.CreatedAt, t.ReceiptUrl, t.CanRefund, t.RefundOfId);
}

public sealed record CardDto(string Brand, string Last4, int ExpMonth, int ExpYear);

/// <summary>The signed-in customer's billing state, for the billing page.</summary>
/// <param name="PaymentsEnabled">Stripe is configured; without it a paid plan cannot be bought.</param>
/// <param name="HasSubscription">The plan is paid through a Stripe subscription (false on Free and on plans the admin granted).</param>
/// <param name="RenewsAt">End of the current period: the next renewal, or when a cancelled plan ends.</param>
/// <param name="CancelAtPeriodEnd">The subscription ends at <paramref name="RenewsAt"/> instead of renewing.</param>
/// <param name="CanManagePayment">Stripe knows this customer, so the billing portal (card, invoices) can be opened.</param>
/// <param name="Card">The default card on file; null when none.</param>
/// <param name="Limits">What the customer may use now: the plan's limits with the platform admin's overrides for them.</param>
/// <param name="Usage">What the customer uses, counted the way the limits are enforced.</param>
public sealed record BillingDto(
    bool PaymentsEnabled, PlanKey Plan, BillingCycle Cycle, CustomerStatus Status, bool HasSubscription, DateTimeOffset? RenewsAt,
    bool CancelAtPeriodEnd, bool CanManagePayment, CardDto? Card, LimitsDto Limits, UsageDto Usage);

/// <summary>Limits in force for a customer; null = unlimited.</summary>
public sealed record LimitsDto(int? Accounts, int? Posts, int? Devices, int? Seats)
{
    public static LimitsDto From(EffectiveLimits l) => new(l.Accounts, l.Posts, l.Devices, l.Seats);
}

/// <param name="Accounts">Accounts connected through the extension, all the customer's workspaces (the sample accounts do not count).</param>
/// <param name="PostsLast24h">Posts published in the last 24 hours, all the customer's workspaces: the plan's posts-per-day window.</param>
/// <param name="Devices">Devices in the customer's busiest workspace: devices are limited per workspace.</param>
public sealed record UsageDto(int Accounts, int PostsLast24h, int Devices);

/// <summary>The outcome of choosing a plan: the plan changed already, or the customer must pay first at <paramref name="CheckoutUrl"/>.</summary>
public sealed record PlanChangeDto(UserDto User, string? CheckoutUrl);

public sealed record UrlDto(string Url);

public sealed record AuthResultDto(string Token, DateTimeOffset ExpiresAt, UserDto User);

/// <param name="Role">The signed-in user's role here (owner for their own workspaces).</param>
/// <param name="Members">People with access, owner included.</param>
/// <param name="Limits">What the workspace's OWNER may use (their plan and the admin's overrides for them): the limits apply to
/// everyone working in it, whatever plan a member has themselves.</param>
/// <param name="AdvancedAntiBan">The owner's plan includes the advanced anti-ban settings.</param>
/// <param name="Notifications">The owner's plan includes Telegram/LINE notifications.</param>
/// <param name="AutoReply">The owner's plan includes auto-reply rules.</param>
/// <param name="ClientReports">The owner's plan includes shareable client reports (Agency).</param>
public sealed record WorkspaceDto(
    Guid Id, string Name, int Posts7, int Members, WorkspaceRole Role, LimitsDto Limits, bool AdvancedAntiBan, bool Notifications,
    bool AutoReply, bool ClientReports)
{
    public static WorkspaceDto From(Workspace w, int posts7, int members, WorkspaceRole role, LimitsDto limits, User owner) =>
        new(w.Id, w.Name, posts7, members, role, limits, owner.HasAdvancedAntiBan, owner.HasNotifications, owner.HasAutoReply, owner.HasClientReports);
}

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
    DateTimeOffset? PublishedAt,
    Guid? ScheduleId,
    Guid? CollectionPostId,
    Guid? LinkId,
    string? Code,
    string? TargetUrl,
    bool IsTest)
{
    public static PostDto From(Post p) =>
        new(p.Id, p.AccountId, p.Platform, p.Target, p.Content, p.MediaIds, p.ScheduledAt, p.Status, p.FailureCode,
            p.FailureDetail, p.PublishedAt, p.ScheduleId, p.CollectionPostId, p.LinkId, p.Code, p.TargetUrl, p.IsTest);
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

/// <summary>The advanced anti-ban numbers (Pro and above; lower plans keep the stored values).</summary>
public sealed record AdvancedAntiBanDto(
    int MinGap, int DailyAll, int BlockMin, int BlockMax, int FailStreak, int RecentAvoid, int Cooldown, bool Focus, int AutoOffFails,
    int StopFailPct)
{
    public static AdvancedAntiBanDto From(AdvancedAntiBanSettings s) =>
        new(s.MinGap, s.DailyAll, s.BlockMin, s.BlockMax, s.FailStreak, s.RecentAvoid, s.Cooldown, s.Focus, s.AutoOffFails, s.StopFailPct);

    public AdvancedAntiBanSettings ToSettings() => new()
    {
        MinGap = MinGap, DailyAll = DailyAll, BlockMin = BlockMin, BlockMax = BlockMax, FailStreak = FailStreak,
        RecentAvoid = RecentAvoid, Cooldown = Cooldown, Focus = Focus, AutoOffFails = AutoOffFails, StopFailPct = StopFailPct,
    };
}

public sealed record AntiBanDto(
    int Min, int Max, PlatformLimitsDto Limits, bool Typing, bool Scroll, bool Shuffle, bool AutoPause, bool Warmup, AdvancedAntiBanDto Advanced)
{
    public static AntiBanDto From(AntiBanSettings s) =>
        new(s.Min, s.Max, PlatformLimitsDto.From(s.Limits), s.Typing, s.Scroll, s.Shuffle, s.AutoPause, s.Warmup,
            AdvancedAntiBanDto.From(s.Advanced));

    public AntiBanSettings ToSettings() => new()
    {
        Min = Min, Max = Max, Limits = Limits.ToSettings(),
        Typing = Typing, Scroll = Scroll, Shuffle = Shuffle, AutoPause = AutoPause, Warmup = Warmup,
        Advanced = Advanced?.ToSettings() ?? new AdvancedAntiBanSettings(), // an old client may omit it
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
/// <param name="JobsPaused">Set in the web app: the browser takes no posts scheduled on the web.</param>
/// <param name="AutoPausedUntil">The engine paused the browser itself (Facebook blocked it, or posts kept failing) until then; null when it is not paused.</param>
/// <param name="AutoPauseReason">Why, in Thai, while <paramref name="AutoPausedUntil"/> is set.</param>
public sealed record DeviceDto(
    Guid Id, string Name, string Browser, string Version, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Online, Guid? AccountId,
    bool JobsPaused, DateTimeOffset? AutoPausedUntil = null, string? AutoPauseReason = null)
{
    public static DeviceDto From(Device d, Guid? accountId, DateTimeOffset now) =>
        new(d.Id, d.Name, d.Browser, d.Version, d.CreatedAt, d.LastSeenAt, d.IsOnline(now), accountId, d.JobsPaused,
            d.IsAutoPaused(now) ? d.AutoPausedUntil : null, d.IsAutoPaused(now) ? d.AutoPauseReason : null);
}

/// <param name="MaxDevices">The owner's plan limit; null = unlimited.</param>
public sealed record PairingCodeDto(string Code, DateTimeOffset ExpiresAt, int? MaxDevices);

/// <summary>Returned once to the extension; only its hash is stored.</summary>
public sealed record PairResultDto(string DeviceKey, Guid DeviceId, string DeviceName, Guid WorkspaceId, string WorkspaceName, Guid AccountId);

/// <param name="Online">False while the offline simulation holds the workspace offline: take no jobs.</param>
/// <param name="JobsPaused">Paused in the web app, or by the engine itself (<paramref name="AutoPausedUntil"/>): take no jobs (state and settings still sync).</param>
/// <param name="AutoPausedUntil">The engine's own pause (a Facebook block, posts that kept failing) lasts until then; null when there is none.</param>
public sealed record DeviceStatusDto(
    Guid DeviceId, string DeviceName, Guid WorkspaceId, string WorkspaceName, Guid? AccountId, int Groups, bool Online, AntiBanDto AntiBan,
    bool JobsPaused, DateTimeOffset? AutoPausedUntil = null, string? AutoPauseReason = null);

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
    IReadOnlyList<CustomerDeviceDto> Devices, string? Note, int Workspaces, LimitOverridesDto Limits,
    bool HasSubscription, DateTimeOffset? RenewsAt, bool CancelAtPeriodEnd);

public sealed record RevenueMonthDto(int Year, int Month, decimal Amount);

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
    bool PaymentsConnected,
    int EventStreams, int DeviceWaits, long EventsPublished, long EventsDropped);

/// <summary>
/// One activity-log line. From/To are values (plan keys, statuses, amounts), not display text. A change Stripe made
/// on its own (a subscription that ended, a refund from its dashboard) has an empty ActorId and ActorEmail.
/// </summary>
public sealed record AuditEntryDto(
    Guid Id, DateTimeOffset At, AuditAction Action, Guid ActorId, string ActorEmail, Guid? CustomerId, string? CustomerEmail,
    string? From, string? To);

/// <param name="Basic">Paying customers per plan (active or past due); Pro and Agency likewise.</param>
/// <param name="Revenue">Charges minus refunds, the last 12 months, oldest first.</param>
public sealed record AdminSummaryDto(int Basic, int Pro, int Agency, IReadOnlyList<RevenueMonthDto> Revenue);

public sealed record AdminJobDto(
    Guid PostId, Guid CustomerId, string Customer, Platform Platform, string Target, string Content, DateTimeOffset ScheduledAt,
    PostStatus Status, FailureCode? FailureCode);

public sealed record PromoDto(string Code, string Discount, int Uses, DateTimeOffset ExpiresAt, bool Active)
{
    public static PromoDto From(Promo p) => new(p.Code, p.Discount, p.Uses, p.ExpiresAt, p.Active);
}

/// <summary>The extension settings of one device (client/lib/shared.js shape), as saved last.</summary>
/// <param name="Revision">0 = nothing saved yet.</param>
/// <param name="Settings">null until the device or the web app saves something.</param>
/// <param name="UpdatedByDevice">The last change came from the extension (false: from the web app).</param>
/// <param name="HasContent">Holds real data (groups, posts or media), not just an empty campaign.</param>
public sealed record ExtensionConfigDto(
    Guid DeviceId, int Revision, System.Text.Json.JsonElement? Settings, DateTimeOffset? UpdatedAt, bool UpdatedByDevice, bool HasContent);

public sealed record ConfigSavedDto(int Revision, DateTimeOffset? UpdatedAt);

/// <summary>One stored media file in the extension's own record shape: data is a data URL.</summary>
public sealed record ExtensionImageDto(string Name, string Type, string Data);

public sealed record MissingImagesDto(IReadOnlyList<string> Missing);

/// <param name="T">The extension's timestamp, Unix milliseconds.</param>
/// <param name="Level">info, success, warn or error.</param>
public sealed record DeviceLogDto(long T, string Level, string Msg)
{
    public static DeviceLogDto From(DeviceLog l) => new(l.T, l.Level, l.Message);
}

/// <summary>What the web app shows of a device's run: presence, its last reported state and log.</summary>
/// <param name="State">The extension's "state" (running, campaign rounds, next times, pauses); null before the first sync.</param>
/// <param name="Revision">The revision of the settings saved on the server.</param>
public sealed record DeviceLiveDto(
    Guid DeviceId, bool Online, DateTimeOffset? LastSeenAt, string Version, System.Text.Json.JsonElement? State,
    DateTimeOffset? StateAt, int Revision, IReadOnlyList<DeviceLogDto> Logs);

/// <param name="Result">The extension's answer ({ ok, error, ... }) once Status is done.</param>
public sealed record DeviceCommandDto(Guid Id, string Cmd, CommandStatus Status, System.Text.Json.JsonElement? Result, DateTimeOffset CreatedAt)
{
    public static DeviceCommandDto From(DeviceCommand c) =>
        new(c.Id, c.Cmd, c.Status, Common.ExtensionSettings.Element(c.Result), c.CreatedAt);
}

public sealed record DeviceCommandItemDto(Guid Id, string Cmd, System.Text.Json.JsonElement Args);

/// <param name="Revision">The server's settings revision; the device pulls when it differs from its own.</param>
/// <param name="HasContent">False: the device may upload its own settings (first sync).</param>
/// <param name="Commands">Commands to run now (only when asked for).</param>
public sealed record DeviceSyncDto(int Revision, bool HasContent, IReadOnlyList<DeviceCommandItemDto> Commands);

/// <summary>One line of the workspace's event stream (see DeviceEventType for the types and payloads).</summary>
public sealed record DeviceEventDto(long Seq, Guid DeviceId, string Type, System.Text.Json.JsonElement Payload, DateTimeOffset At)
{
    public static DeviceEventDto From(DeviceEvent e) =>
        new(e.Seq, e.DeviceId, e.Type, Common.ExtensionSettings.Element(e.Payload) ?? default, e.At);
}

/// <param name="Head">The newest Seq of the workspace right now; a stream resumes from it.</param>
/// <param name="Events">Events after the Seq asked for, oldest first.</param>
/// <param name="More">True when there were more than asked for: ask again from the last Seq.</param>
public sealed record DeviceEventsPageDto(long Head, IReadOnlyList<DeviceEventDto> Events, bool More);
