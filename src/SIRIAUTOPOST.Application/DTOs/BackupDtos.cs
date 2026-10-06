using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.DTOs;

// The backup file (version 2): everything the collection -> link set -> schedule workflow is made of, with ids
// replaced by names. No secrets: the Telegram and LINE tokens and chat ids stay out, and a restore leaves the ones the
// workspace has. The file is what GET backup answers and what POST restore takes.

/// <param name="Key">Ties the copies of one post that sits in several collections together; a post without one is its own.</param>
public sealed record BackupPostDto(
    string Text, IReadOnlyList<Guid> MediaIds, PostApproval Approval, bool Active = true, CollectionPostSettingsDto? Settings = null, string? Key = null);


public sealed record BackupCollectionDto(
    string Name, string Description, string Icon, CollectionSettingsDto Settings, IReadOnlyList<BackupPostDto> Posts);

public sealed record BackupLinkDto(string Name, string Url, string Code, int DailyMax, bool Enabled);

/// <param name="PostAsAccountId">The account of this workspace whose browser posts the links (accounts are not part of a backup).</param>
public sealed record BackupLinkSetDto(string Name, Guid? PostAsAccountId, IReadOnlyList<Guid> AccountIds, IReadOnlyList<BackupLinkDto> Links);

/// <param name="Collection">The collection's name.</param>
/// <param name="LinkSet">The link set's name.</param>
/// <param name="Overrides">Own times per target: a link's address, or "account:&lt;id&gt;".</param>
public sealed record BackupScheduleDto(
    string Name, string Collection, string LinkSet, ScheduleMode Mode, IReadOnlyList<string> Times, int EveryHours, string FirstTime,
    string StartDate, string OnceTime, PostOrder Order, string DripFrom, string DripTo, int DripCount, int BumpHours, int AutoDeleteDays,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Overrides, bool Active, int UtcOffsetMinutes, PostRepeat Repeat = PostRepeat.Recent,
    BumpPlanDto? Bump = null);

/// <param name="Url">The group's address inside its set.</param>
public sealed record BackupGroupNotifyRuleDto(string Url, NotifyChannel Channel, NotifyEventsDto? Events);

/// <param name="LinkSet">The link set's name.</param>
public sealed record BackupSetNotifyRuleDto(string LinkSet, NotifyChannel Channel, NotifyEventsDto? Events, IReadOnlyList<BackupGroupNotifyRuleDto> Groups);

/// <summary>Where and which events are sent, by link set name and group address. Tokens and chat ids are not in it.</summary>
public sealed record BackupNotificationsDto(NotifyChannel Channel, NotifyEventsDto Events, IReadOnlyList<BackupSetNotifyRuleDto> Sets);

/// <param name="Collection">The collection's name the rule is about; null = all.</param>
public sealed record BackupAutoReplyRuleDto(string Keywords, string Reply, string Inbox, string? Collection, bool On);

public sealed record BackupAutoReplyDto(bool On, IReadOnlyList<BackupAutoReplyRuleDto> Rules);

/// <param name="Version">2.</param>
/// <param name="AntiBanAdvanced">The advanced anti-ban numbers; null leaves them as they are on restore.</param>
/// <param name="NotificationRules">null leaves the workspace's rules as they are on restore.</param>
/// <param name="AutoReply">null leaves the workspace's rules as they are on restore.</param>
/// <param name="Posts">Posts of the library that sit in no collection (the ones in a collection are inside it, a post in several carries the same key in each).</param>
public sealed record BackupDto(
    int Version, DateTimeOffset CreatedAt, IReadOnlyList<BackupCollectionDto> Collections, IReadOnlyList<BackupLinkSetDto> LinkSets,
    IReadOnlyList<BackupScheduleDto> Schedules, AdvancedAntiBanDto? AntiBanAdvanced, BackupNotificationsDto? NotificationRules,
    BackupAutoReplyDto? AutoReply, IReadOnlyList<BackupPostDto>? Posts = null);
