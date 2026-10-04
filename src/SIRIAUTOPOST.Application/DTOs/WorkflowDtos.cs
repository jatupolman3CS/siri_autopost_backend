using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
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

/// <param name="PostedCount">Successful posts (not tests) made from it.</param>
public sealed record CollectionPostDto(
    Guid Id, Guid CollectionId, string Text, IReadOnlyList<Guid> MediaIds, PostApproval Approval, int PostedCount, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CollectionPostDto From(CollectionPost p, int postedCount) =>
        new(p.Id, p.CollectionId, p.Text, p.MediaIds, p.Approval, postedCount, p.CreatedAt, p.UpdatedAt);
}

/// <param name="Posts">Oldest first (the newest is last).</param>
/// <param name="ScheduleCount">Schedules that use the collection.</param>
public sealed record CollectionDto(
    Guid Id, string Name, string Description, string Icon, CollectionSettingsDto Settings, IReadOnlyList<CollectionPostDto> Posts, int ScheduleCount)
{
    public static CollectionDto From(PostCollection c, IReadOnlyList<CollectionPostDto> posts, int scheduleCount) =>
        new(c.Id, c.Name, c.Description, c.Icon, CollectionSettingsDto.From(c.Settings), posts, scheduleCount);
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
    Guid Id, string Name, Guid? PostAsAccountId, IReadOnlyList<Guid> AccountIds, IReadOnlyList<SetLinkDto> Links, int ScheduleCount)
{
    public static LinkSetDto From(LinkSet set, IReadOnlyList<SetLink> links, int scheduleCount)
    {
        var seen = new HashSet<string>();
        var dtos = links.Select(l => SetLinkDto.From(l, l.IsValid && !seen.Add(l.Url))).ToList();
        return new LinkSetDto(set.Id, set.Name, set.PostAsAccountId, set.AccountIds, dtos, scheduleCount);
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
    int TargetCount, int PerDay, int UsablePosts, int TodayCount, DateTimeOffset? NextRunAt);

/// <param name="StartDate">"yyyy-MM-dd" in the schedule's local calendar; empty = today.</param>
/// <param name="Overrides">Own times per link (its id, 32 hex digits) or "account:&lt;id&gt;"; empty list = follow the schedule.</param>
/// <param name="UtcOffsetMinutes">The browser's UTC offset.</param>
public sealed record SaveScheduleRequest(
    string? Name, Guid CollectionId, Guid LinkSetId, ScheduleMode Mode, IReadOnlyList<string>? Times, int EveryHours, string? FirstTime,
    string? StartDate, string? OnceTime, PostOrder Order, string? DripFrom, string? DripTo, int DripCount, int BumpHours, int AutoDeleteDays,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Overrides, int UtcOffsetMinutes);

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

public sealed record SharedReportDto(
    string Brand, string WorkspaceName, bool Logo, string Period, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ReportDto Report);

// ---------- backup ----------

/// <summary>What a restore replaced.</summary>
public sealed record RestoreResultDto(int Collections, int LinkSets, int Schedules);
