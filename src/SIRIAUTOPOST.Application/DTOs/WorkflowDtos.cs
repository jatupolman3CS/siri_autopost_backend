using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.Services;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.DTOs;

// The collection -> link set -> schedule workflow, notifications, auto-reply and reports.
// The names and fields are the contract the web app generates its types from.

// ---------- collections ----------

public sealed record CollectionSettingsDto(
    string Hashtags, string PageTags, string Footer, FooterPosition FooterPos, bool Shuffle, bool Watermark, WatermarkPosition WatermarkPos,
    bool RequireApproval)
{
    public static CollectionSettingsDto From(CollectionSettings s) =>
        new(s.Hashtags, s.PageTags, s.Footer, s.FooterPos, s.Shuffle, s.Watermark, s.WatermarkPos, s.RequireApproval);

    public CollectionSettings ToSettings() => new()
    {
        Hashtags = Hashtags, PageTags = PageTags, Footer = Footer, FooterPos = FooterPos, Shuffle = Shuffle, Watermark = Watermark,
        WatermarkPos = WatermarkPos, RequireApproval = RequireApproval,
    };
}

/// <summary>
/// A master post's own settings. Null hashtags, footer and position follow the collection; dates are "yyyy-MM-dd" in the
/// schedule's calendar, weekdays 0 = Sunday to 6 = Saturday (empty = every day), the window "HH:mm", MaxPerDay 0 = no limit.
/// </summary>
public sealed record CollectionPostSettingsDto(
    string? Hashtags, string? Footer, FooterPosition? FooterPos, string? ValidFrom, string? ValidUntil, IReadOnlyList<int>? Weekdays,
    string? TimeFrom, string? TimeTo, int MaxPerDay)
{
    public static CollectionPostSettingsDto From(CollectionPostSettings s) =>
        new(s.Hashtags, s.Footer, s.FooterPos, Date(s.ValidFrom), Date(s.ValidUntil), s.Weekdays, s.TimeFrom, s.TimeTo, s.MaxPerDay);

    public CollectionPostSettings ToSettings() => new()
    {
        Hashtags = Hashtags, Footer = Footer, FooterPos = FooterPos, ValidFrom = ParseDate(ValidFrom), ValidUntil = ParseDate(ValidUntil),
        Weekdays = [.. Weekdays ?? []], TimeFrom = TimeFrom, TimeTo = TimeTo, MaxPerDay = MaxPerDay,
    };

    private static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : throw new DomainException("วันที่ของโพสต์ไม่ถูกต้อง ใช้รูปแบบ ปปปป-ดด-วว");
}

/// <summary>
/// A master post. The numbers count what schedules made from it (tests excluded): <paramref name="PostedCount"/> went out,
/// <paramref name="QueuedCount"/> wait or are being posted, <paramref name="FailedCount"/> failed and were not dismissed.
/// </summary>
/// <param name="CollectionIds">The collections the post sits in (oldest membership first).</param>
public sealed record CollectionPostDto(
    Guid Id, string Text, IReadOnlyList<Guid> MediaIds, PostApproval Approval, bool Active, CollectionPostSettingsDto Settings,
    IReadOnlyList<Guid> CollectionIds, int PostedCount, int QueuedCount, int FailedCount, DateTimeOffset? LastPostedAt, DateTimeOffset? NextAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static CollectionPostDto From(CollectionPost p, IReadOnlyList<Guid> collectionIds, CollectionPostUsage? usage) =>
        new(p.Id, p.Text, p.MediaIds, p.Approval, p.Active, CollectionPostSettingsDto.From(p.Settings), collectionIds,
            usage?.Posted ?? 0, usage?.Queued ?? 0, usage?.Failed ?? 0, usage?.LastPublishedAt, usage?.NextAt, p.CreatedAt, p.UpdatedAt);
}

/// <summary>One post a master post made: where it went, when and how it ended.</summary>
public sealed record CollectionPostActivityDto(
    Guid Id, string Target, string? TargetUrl, PostStatus Status, DateTimeOffset ScheduledAt, DateTimeOffset? PublishedAt, string? FailureDetail,
    Guid? ScheduleId)
{
    public static CollectionPostActivityDto From(Post p) =>
        new(p.Id, p.Target, p.TargetUrl, p.Status, p.ScheduledAt, p.PublishedAt, p.FailureDetail, p.ScheduleId);
}

/// <param name="Posts">Oldest first (the newest is last).</param>
/// <param name="ScheduleCount">Schedules that use the collection.</param>
public sealed record CollectionDto(
    Guid Id, string Name, string Description, string Icon, CollectionSettingsDto Settings, IReadOnlyList<CollectionPostDto> Posts, int ScheduleCount, bool Active)
{
    public static CollectionDto From(PostCollection c, IReadOnlyList<CollectionPostDto> posts, int scheduleCount) =>
        new(c.Id, c.Name, c.Description, c.Icon, CollectionSettingsDto.From(c.Settings), posts, scheduleCount, c.Active);
}

/// <summary>What the approval buttons do: the author asks, an admin approves or sends it back.</summary>
public enum ApprovalAction
{
    Request,
    Approve,
    Reject,
}

/// <summary>One post to add to a collection (the AI writer adds up to 20 at once).</summary>
public sealed record CollectionPostInput(string Text, IReadOnlyList<Guid>? MediaIds);

// ---------- link sets ----------

/// <param name="Valid">A real Facebook group address (links that are not valid are never posted to).</param>
/// <param name="Duplicate">Another link of the same set has the same address (the later one is flagged).</param>
public sealed record SetLinkDto(
    Guid Id, string Name, string Url, string Code, int DailyMax, bool Enabled, LinkHealth Health, int FailStreak, bool Valid, bool Duplicate)
{
    public static SetLinkDto From(SetLink l, bool duplicate) =>
        new(l.Id, l.Name, l.Url, l.Code, l.DailyMax, l.Enabled, l.Health, l.FailStreak, l.IsValid, duplicate);
}

/// <param name="PostAsAccountId">The Facebook account whose browser posts the links; null = the first connected one.</param>
/// <param name="AccountIds">Other accounts that post the same content to their default target.</param>
/// <param name="Links">In their order.</param>
public sealed record LinkSetDto(
    Guid Id, string Name, Guid? PostAsAccountId, IReadOnlyList<Guid> AccountIds, IReadOnlyList<SetLinkDto> Links, int ScheduleCount, bool Active)
{
    public static LinkSetDto From(LinkSet set, IReadOnlyList<SetLink> links, int scheduleCount)
    {
        var seen = new HashSet<string>(FacebookGroupUrl.Comparer); // /groups/ABC and /groups/abc are one group
        var dtos = links.Select(l => SetLinkDto.From(l, l.IsValid && !seen.Add(l.Url))).ToList();
        return new LinkSetDto(set.Id, set.Name, set.PostAsAccountId, set.AccountIds, dtos, scheduleCount, set.Active);
    }
}

public sealed record BulkLinksResultDto(int Added, int Duplicates, int Recoded, int Invalid, LinkSetDto Set);

/// <summary>One line of the CSV import: set,name,url,code.</summary>
public sealed record CsvLinkRow(string Set, string? Name, string? Url, string? Code);

public sealed record CsvImportResultDto(int Links, int Sets, int Invalid);

// ---------- schedules ----------

/// <param name="Slots">The posting times of one day.</param>
/// <param name="TargetCount">Links in use plus other accounts of the link set.</param>
/// <param name="PerDay">Posts per matching day: for each target its own times or the schedule's.</param>
/// <param name="UsablePosts">Collection posts that may be scheduled (approved ones when the collection requires approval).</param>
/// <param name="TodayCount">Posts of the schedule on its local today.</param>
/// <param name="NextRunAt">The earliest queued post still to go out.</param>
public sealed record ScheduleDto(
    Guid Id, string Name, Guid CollectionId, Guid LinkSetId, ScheduleMode Mode, IReadOnlyList<string> Times, int EveryHours, string FirstTime,
    string StartDate, string OnceTime, PostOrder Order, string DripFrom, string DripTo, int DripCount, int BumpHours, int AutoDeleteDays,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Overrides, bool Active, int UtcOffsetMinutes, IReadOnlyList<string> Slots,
    int TargetCount, int PerDay, int UsablePosts, int TodayCount, DateTimeOffset? NextRunAt, bool StartNow = false,
    PostRepeat Repeat = PostRepeat.Recent);

/// <param name="StartDate">"yyyy-MM-dd" in the schedule's local calendar; empty = today.</param>
/// <param name="Overrides">Own times per link (its id, 32 hex digits) or "account:&lt;id&gt;"; empty list = follow the schedule.</param>
/// <param name="UtcOffsetMinutes">The browser's UTC offset.</param>
/// <param name="StartNow">
/// True = start when the schedule is created: the groups go out one after the other from this moment (the start date and,
/// for Once, the time are ignored), then the regular times go on. False (default) = wait for the times.
/// </param>
/// <param name="Repeat">
/// With shuffle, whether a group may get a post again: recent (default) = not its last few, any = yes, even within a day,
/// never = not until it has had every post. Ignored by rotate.
/// </param>
public sealed record SaveScheduleRequest(
    string? Name, Guid CollectionId, Guid LinkSetId, ScheduleMode Mode, IReadOnlyList<string>? Times, int EveryHours, string? FirstTime,
    string? StartDate, string? OnceTime, PostOrder Order, string? DripFrom, string? DripTo, int DripCount, int BumpHours, int AutoDeleteDays,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Overrides, int UtcOffsetMinutes, bool StartNow = false,
    PostRepeat Repeat = PostRepeat.Recent);

public sealed record ScheduleCreatedDto(ScheduleDto Schedule, int Created, DateTimeOffset? FirstAt, DateTimeOffset? LastAt);

// ---------- notifications ----------

public sealed record NotifyEventsDto(bool Success, bool Fail, bool Shot, bool Round, bool StartStop, bool Block, bool Offline, bool Quota)
{
    public static NotifyEventsDto From(NotifyEventSet e) => new(e.Success, e.Fail, e.Shot, e.Round, e.StartStop, e.Block, e.Offline, e.Quota);

    public NotifyEventSet ToSettings() => new()
    {
        Success = Success, Fail = Fail, Shot = Shot, Round = Round, StartStop = StartStop, Block = Block, Offline = Offline, Quota = Quota,
    };
}

/// <param name="Token">Request only: null keeps the stored token, "" clears it. Responses never carry it.</param>
/// <param name="HasToken">A token is stored.</param>
public sealed record TelegramSettingsDto(bool On, string? Token, bool HasToken, string ChatId);

/// <param name="Token">Request only: null keeps the stored token, "" clears it. Responses never carry it.</param>
/// <param name="HasToken">A token is stored.</param>
public sealed record LineSettingsDto(bool On, string? Token, bool HasToken, string To);

public sealed record GroupNotifyRuleDto(NotifyChannel Channel, NotifyEventsDto? Events);

/// <param name="Groups">Rules of single groups, keyed by the link's id.</param>
public sealed record SetNotifyRuleDto(Guid LinkSetId, NotifyChannel Channel, NotifyEventsDto? Events, IReadOnlyDictionary<string, GroupNotifyRuleDto> Groups);

public sealed record NotificationSettingsDto(
    TelegramSettingsDto Telegram, LineSettingsDto Line, NotifyChannel Channel, NotifyEventsDto Events, IReadOnlyList<SetNotifyRuleDto> Sets,
    bool CommandsOn, string CommandsUsers)
{
    /// <summary>The settings as the API shows them: tokens never leave, only whether one is stored.</summary>
    public static NotificationSettingsDto From(NotificationSettings n) => new(
        new TelegramSettingsDto(n.Telegram.On, null, n.Telegram.Token.Length > 0, n.Telegram.ChatId),
        new LineSettingsDto(n.Line.On, null, n.Line.Token.Length > 0, n.Line.To),
        n.Channel,
        NotifyEventsDto.From(n.Events),
        n.Sets.Select(kv => new SetNotifyRuleDto(
            kv.Key, kv.Value.Channel, kv.Value.Events is null ? null : NotifyEventsDto.From(kv.Value.Events),
            kv.Value.Groups.ToDictionary(
                g => g.Key.ToString(),
                g => new GroupNotifyRuleDto(g.Value.Channel, g.Value.Events is null ? null : NotifyEventsDto.From(g.Value.Events)))))
            .ToList(),
        n.CommandsOn,
        n.CommandsUsers);

    /// <summary>
    /// The saved settings: a null token keeps the one <paramref name="current"/> has, "" clears it, anything else
    /// replaces it.
    /// </summary>
    public NotificationSettings ToSettings(NotificationSettings current)
    {
        var settings = new NotificationSettings
        {
            Telegram = new TelegramChannel { On = Telegram.On, Token = Telegram.Token ?? current.Telegram.Token, ChatId = Telegram.ChatId ?? "" },
            Line = new LineChannel { On = Line.On, Token = Line.Token ?? current.Line.Token, To = Line.To ?? "" },
            Channel = Channel,
            Events = Events.ToSettings(),
            CommandsOn = CommandsOn,
            CommandsUsers = CommandsUsers ?? "",
        };
        foreach (var set in Sets ?? [])
        {
            var rule = new SetNotifyRule { Channel = set.Channel, Events = set.Events?.ToSettings() };
            foreach (var (key, group) in set.Groups ?? new Dictionary<string, GroupNotifyRuleDto>())
            {
                if (!Guid.TryParse(key, out var linkId)) throw new DomainException("รหัสกลุ่มในกฎแจ้งเตือนไม่ถูกต้อง");
                rule.Groups[linkId] = new GroupNotifyRule { Channel = group.Channel, Events = group.Events?.ToSettings() };
            }
            settings.Sets[set.LinkSetId] = rule;
        }
        return settings;
    }
}

/// <param name="Channel">"tg" or "line".</param>
public sealed record NotifyTestRequest(string Channel);

public sealed record NotifyTestResultDto(bool Ok, string? Message);

/// <param name="Token">The bot token to look chats up with; null = the stored one.</param>
public sealed record FindTelegramChatsRequest(string? Token);

public sealed record TelegramChatDto(string Id, string Title);

public sealed record TelegramChatsDto(IReadOnlyList<TelegramChatDto> Chats);

// ---------- auto-reply ----------

public sealed record AutoReplyRuleDto(Guid Id, string Keywords, string Reply, string Inbox, string Scope, bool On);

public sealed record AutoReplyDto(bool On, IReadOnlyList<AutoReplyRuleDto> Rules)
{
    public static AutoReplyDto From(AutoReplySettings a) =>
        new(a.On, a.Rules.Select(r => new AutoReplyRuleDto(r.Id, r.Keywords, r.Reply, r.Inbox, r.Scope, r.On)).ToList());

    public AutoReplySettings ToSettings() => new()
    {
        On = On,
        Rules = (Rules ?? []).Select(r => new AutoReplyRule
        {
            Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id, Keywords = r.Keywords, Reply = r.Reply, Inbox = r.Inbox, Scope = r.Scope, On = r.On,
        }).ToList(),
    };
}

// ---------- reports ----------

/// <param name="LinkId">Null for posts that did not come from a link of a link set.</param>
/// <param name="Pending">Waiting for the group's admin to approve.</param>
/// <param name="Rate">round(posted / (posted + failed) * 100); 100 when there is nothing yet.</param>
public sealed record ReportGroupDto(
    Guid? LinkId, string Name, string? Url, Platform Platform, int Posted, int Pending, int Failed, int Rate, bool Enabled, LinkHealth Health);

public sealed record ReportPostDto(Guid CollectionPostId, string Text, int Used);

public sealed record ReportDto(int Days, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ReportGroupDto> Groups, IReadOnlyList<ReportPostDto> Posts);

/// <param name="Period">"week" or "month".</param>
public sealed record ShareReportRequest(string Brand, string Period, bool Logo);

/// <param name="Path">"/report/&lt;token&gt;": the page of the web app that shows it.</param>
public sealed record ReportShareDto(string Token, string Path, DateTimeOffset ExpiresAt);

/// <summary>A live client report link, for the admins who manage them. <paramref name="Path"/> carries the token: it is the key to the report.</summary>
/// <param name="Id">What <c>DELETE reports/shares/{id}</c> takes.</param>
/// <param name="Period">"week" or "month".</param>
public sealed record ReportShareSummaryDto(
    Guid Id, string Brand, string Period, bool Logo, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Path);

public sealed record SharedReportDto(
    string Brand, string WorkspaceName, bool Logo, string Period, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ReportDto Report);

// ---------- backup ----------

/// <summary>What a restore replaced.</summary>
public sealed record RestoreResultDto(int Collections, int LinkSets, int Schedules);
