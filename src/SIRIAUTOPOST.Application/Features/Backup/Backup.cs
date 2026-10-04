using System.Globalization;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.Services;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Features.Backup;

// Backup and restore of the workflow: collections and their posts, link sets and their links, schedules, the advanced
// anti-ban numbers, notification rules and auto-reply rules. Admins only. Ids are replaced by names in the file, so it
// can be restored into the same workspace (the library's files and the accounts are the workspace's own).

public sealed record GetBackupQuery(Guid WorkspaceId) : IQuery<BackupDto>;

public sealed class GetBackupQueryHandler(
    IWorkspaceRepository workspaces, ICollectionRepository collections, ICollectionPostRepository collectionPosts, ILinkSetRepository linkSets,
    ISetLinkRepository setLinks, IScheduleRepository schedules, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetBackupQuery, BackupDto>
{
    public const int Version = 2;

    public async Task<BackupDto> HandleAsync(GetBackupQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var allCollections = await collections.ListAsync(ws.Id, ct);
        var postsBy = (await collectionPosts.ListAsync(ws.Id, ct)).ToLookup(p => p.CollectionId);
        var allSets = await linkSets.ListAsync(ws.Id, ct);
        var linksBy = (await setLinks.ListAsync(ws.Id, ct)).ToLookup(l => l.LinkSetId);

        // Names tie the parts together; two with the same name would be told apart by a number.
        var collectionNames = UniqueNames(allCollections.Select(c => c.Name));
        var setNames = UniqueNames(allSets.Select(s => s.Name));
        var collectionName = allCollections.Select((c, i) => (c.Id, Name: collectionNames[i])).ToDictionary(x => x.Id, x => x.Name);
        var setName = allSets.Select((s, i) => (s.Id, Name: setNames[i])).ToDictionary(x => x.Id, x => x.Name);
        var linkUrl = allSets.SelectMany(s => linksBy[s.Id]).ToDictionary(l => l.Id, l => l.Url);

        var backupCollections = allCollections.Select((c, i) => new BackupCollectionDto(
            collectionNames[i], c.Description, c.Icon, CollectionSettingsDto.From(c.Settings),
            postsBy[c.Id].Select(p => new BackupPostDto(p.Text, p.MediaIds, p.Approval)).ToList())).ToList();
        var backupSets = allSets.Select((s, i) => new BackupLinkSetDto(
            setNames[i], s.PostAsAccountId, s.AccountIds,
            linksBy[s.Id].Select(l => new BackupLinkDto(l.Name, l.Url, l.Code, l.DailyMax, l.Enabled)).ToList())).ToList();

        var backupSchedules = new List<BackupScheduleDto>();
        foreach (var s in await schedules.ListAsync(ws.Id, ct))
        {
            if (!collectionName.TryGetValue(s.CollectionId, out var cName) || !setName.TryGetValue(s.LinkSetId, out var lName)) continue;
            var ownLinks = linksBy[s.LinkSetId].ToDictionary(l => Schedule.LinkKey(l.Id), l => l.Url);
            var overrides = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var (key, times) in s.Overrides)
            {
                if (key.StartsWith("account:", StringComparison.Ordinal)) overrides[key] = times;
                else if (ownLinks.TryGetValue(key, out var url)) overrides[url] = times; // a link that is gone has nothing to restore
            }
            backupSchedules.Add(new BackupScheduleDto(
                s.Name, cName, lName, s.Mode, s.Times, s.EveryHours, s.FirstTime, s.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.OnceTime, s.Order, s.DripFrom, s.DripTo, s.DripCount, s.BumpHours, s.AutoDeleteDays, overrides, s.Active, s.UtcOffsetMinutes));
        }

        var n = ws.Notifications;
        var notifications = new BackupNotificationsDto(
            n.Channel, NotifyEventsDto.From(n.Events),
            n.Sets.Where(kv => setName.ContainsKey(kv.Key)).Select(kv => new BackupSetNotifyRuleDto(
                setName[kv.Key], kv.Value.Channel, kv.Value.Events is null ? null : NotifyEventsDto.From(kv.Value.Events),
                kv.Value.Groups.Where(g => linkUrl.ContainsKey(g.Key)).Select(g => new BackupGroupNotifyRuleDto(
                    linkUrl[g.Key], g.Value.Channel, g.Value.Events is null ? null : NotifyEventsDto.From(g.Value.Events))).ToList())).ToList());
        var autoReply = new BackupAutoReplyDto(ws.AutoReply.On, ws.AutoReply.Rules.Select(r => new BackupAutoReplyRuleDto(
            r.Keywords, r.Reply, r.Inbox,
            Guid.TryParse(r.Scope, out var scope) && collectionName.TryGetValue(scope, out var name) ? name : null, r.On)).ToList());

        return new BackupDto(
            Version, clock.GetUtcNow(), backupCollections, backupSets, backupSchedules, AdvancedAntiBanDto.From(ws.AntiBan.Advanced), notifications,
            autoReply);
    }

    /// <summary>The names, with " (2)", " (3)"... after a name that already came before.</summary>
    internal static List<string> UniqueNames(IEnumerable<string> names)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var name in names)
        {
            var candidate = name;
            for (var i = 2; !seen.Add(candidate); i++) candidate = $"{name} ({i})";
            result.Add(candidate);
        }
        return result;
    }
}

/// <summary>
/// Replaces the workspace's collections (with their posts), link sets (with their links) and schedules by the file's,
/// restores the advanced anti-ban numbers, notification rules and auto-reply rules the plan allows (tokens and chat ids
/// are never touched), then queues the posts of the active schedules. Everything is checked first and saved in one go:
/// a file that does not pass changes nothing. The replaced schedules' queued posts are deleted; what already went out
/// stays in the history.
/// </summary>
public sealed record RestoreBackupCommand(Guid WorkspaceId, BackupDto Backup) : ICommand<RestoreResultDto>;

public sealed class RestoreBackupCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts, IMediaRepository media, ICollectionRepository collections,
    ICollectionPostRepository collectionPosts, ILinkSetRepository linkSets, ISetLinkRepository setLinks, IScheduleRepository schedules,
    IPostRepository posts, ScheduleTopUp topUp, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RestoreBackupCommand, RestoreResultDto>
{
    public async Task<RestoreResultDto> HandleAsync(RestoreBackupCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var b = c.Backup;
        if (b.Version != GetBackupQueryHandler.Version)
            throw new DomainException($"ไฟล์สำรองเวอร์ชัน {b.Version} ใช้ไม่ได้ ต้องเป็นไฟล์เวอร์ชัน {GetBackupQueryHandler.Version}");
        if (b.Collections.Count > PostCollection.MaxPerWorkspace) throw new DomainException($"ไฟล์มีชุดโพสต์เกิน {PostCollection.MaxPerWorkspace} ชุด");
        if (b.LinkSets.Count > LinkSet.MaxPerWorkspace) throw new DomainException($"ไฟล์มีชุดลิงก์เกิน {LinkSet.MaxPerWorkspace} ชุด");
        if (b.Schedules.Count > Schedule.MaxPerWorkspace) throw new DomainException($"ไฟล์มีตารางโพสต์เกิน {Schedule.MaxPerWorkspace} ตาราง");
        if (b.Collections.Sum(x => x.Posts.Count) > CollectionPost.MaxPerWorkspace)
            throw new DomainException($"ไฟล์มีโพสต์ในชุดโพสต์เกิน {CollectionPost.MaxPerWorkspace:N0} โพสต์");
        RequireUnique(b.Collections.Select(x => x.Name), "ชื่อชุดโพสต์");
        RequireUnique(b.LinkSets.Select(x => x.Name), "ชื่อชุดลิงก์");

        var owner = await users.OwnerOfAsync(ws, ct);
        var now = clock.GetUtcNow();
        var library = (await media.ListAsync(ws.Id, ct)).Select(m => m.Id).ToHashSet(); // a file the library no longer has is dropped
        var accountList = await accounts.ListAsync(ws.Id, ct);
        var known = accountList.Select(a => a.Id).ToHashSet();
        var facebook = accountList.Where(a => a.Platform == Platform.Fb).Select(a => a.Id).ToHashSet();

        // ---- build the new parts in memory (nothing is touched until they all pass) ----
        var newCollections = new Dictionary<string, PostCollection>(StringComparer.Ordinal);
        var newPosts = new List<CollectionPost>();
        var tick = 0;
        for (var i = 0; i < b.Collections.Count; i++)
        {
            var bc = b.Collections[i];
            var collection = PostCollection.Create(ws.Id, bc.Name, bc.Description, now, i);
            // The posts start as drafts of a collection that asks for approval, so each can be moved to the state it had.
            collection.Update(bc.Name, bc.Description, bc.Icon, bc.Settings.ToSettings().WithApproval());
            foreach (var bp in bc.Posts)
            {
                var post = CollectionPost.Create(collection, bp.Text, (bp.MediaIds ?? []).Where(library.Contains), now.AddMilliseconds(tick++));
                if (bp.Approval != PostApproval.Draft) post.RequestApproval(now);
                if (bp.Approval == PostApproval.Approved) post.Approve(now);
                newPosts.Add(post);
            }
            collection.Update(bc.Name, bc.Description, bc.Icon, bc.Settings.ToSettings());
            newCollections[collection.Name] = collection;
        }

        var newSets = new Dictionary<string, LinkSet>(StringComparer.Ordinal);
        var newLinks = new List<SetLink>();
        var linksOfSet = new Dictionary<Guid, List<SetLink>>();
        for (var i = 0; i < b.LinkSets.Count; i++)
        {
            var bs = b.LinkSets[i];
            var postAs = bs.PostAsAccountId is { } id && facebook.Contains(id) ? id : (Guid?)null;
            var set = LinkSet.Create(ws.Id, bs.Name, postAs, now, i);
            set.Update(bs.Name, postAs, (bs.AccountIds ?? []).Where(known.Contains));
            if (bs.Links.Count > LinkSet.MaxLinks) throw new DomainException($"ชุดลิงก์ \"{bs.Name}\" มีลิงก์เกิน {LinkSet.MaxLinks} ลิงก์");
            var own = new List<SetLink>();
            for (var k = 0; k < bs.Links.Count; k++)
            {
                var bl = bs.Links[k];
                var link = SetLink.Create(ws.Id, set.Id, bl.Name, bl.Url, bl.Code, bl.DailyMax, now, k);
                if (!bl.Enabled) link.DisableManually();
                own.Add(link);
            }
            newLinks.AddRange(own);
            linksOfSet[set.Id] = own;
            newSets[set.Name] = set;
        }

        var newSchedules = new List<Schedule>();
        foreach (var bsc in b.Schedules)
        {
            // Each a millisecond after the one before it, so the list comes back in the file's order.
            if (!newCollections.TryGetValue((bsc.Collection ?? "").Trim(), out var collection))
                throw new DomainException($"ตาราง \"{bsc.Name}\" อ้างถึงชุดโพสต์ \"{bsc.Collection}\" ที่ไม่มีในไฟล์");
            if (!newSets.TryGetValue((bsc.LinkSet ?? "").Trim(), out var set))
                throw new DomainException($"ตาราง \"{bsc.Name}\" อ้างถึงชุดลิงก์ \"{bsc.LinkSet}\" ที่ไม่มีในไฟล์");
            var start = ParseDate(bsc.StartDate, bsc.UtcOffsetMinutes, now, bsc.Name);
            var schedule = Schedule.Create(
                ws.Id, bsc.Name, collection.Id, set.Id, bsc.Mode, bsc.Times, bsc.EveryHours, bsc.FirstTime, start, bsc.OnceTime, bsc.Order,
                bsc.DripFrom, bsc.DripTo, bsc.DripCount, bsc.BumpHours, bsc.AutoDeleteDays,
                TranslateOverrides(bsc.Overrides, set, linksOfSet[set.Id]), bsc.UtcOffsetMinutes, now.AddMilliseconds(newSchedules.Count));
            if (!bsc.Active) schedule.SetActive(false);
            newSchedules.Add(schedule);
        }

        // ---- settings ----
        if (b.AntiBanAdvanced is { } advanced && owner.HasAdvancedAntiBan)
        {
            var settings = AntiBanDto.From(ws.AntiBan).ToSettings();
            settings.Advanced = advanced.ToSettings();
            ws.UpdateAntiBan(settings, advancedAllowed: true);
        }
        if (b.NotificationRules is { } rules && owner.HasNotifications)
            ws.UpdateNotifications(BuildNotifications(ws.Notifications, rules, newSets, linksOfSet));
        if (b.AutoReply is { } reply && owner.HasAutoReply) ws.UpdateAutoReply(BuildAutoReply(reply, newCollections));

        // ---- replace ----
        // One restore of a workspace at a time, and all or nothing: when the posts of the new schedules do not fit in the
        // workspace's queue (QueueFullException) the whole restore is rolled back and refused.
        await uow.ExecuteInTransactionAsync($"restore:{ws.Id:N}", async () =>
        {
            var oldSchedules = await schedules.ListAsync(ws.Id, ct);
            foreach (var s in oldSchedules) posts.RemoveRange(await posts.ListFutureQueuedByScheduleAsync(s.Id, now, ct));
            schedules.RemoveRange(oldSchedules);
            collections.RemoveRange(await collections.ListAsync(ws.Id, ct));
            linkSets.RemoveRange(await linkSets.ListAsync(ws.Id, ct));
            foreach (var collection in newCollections.Values) collections.Add(collection);
            collectionPosts.AddRange(newPosts);
            foreach (var set in newSets.Values) linkSets.Add(set);
            setLinks.AddRange(newLinks);
            foreach (var s in newSchedules) schedules.Add(s);
            await uow.SaveChangesAsync(ct);

            foreach (var s in newSchedules.Where(s => s.Active))
            {
                try
                {
                    await topUp.GenerateAsync(ws.Id, s.Id, ct);
                }
                catch (DomainException ex) when (ex is not QueueFullException)
                {
                    // The restore itself is done; a schedule that cannot be queued now is tried again with the next claim.
                }
            }
        }, ct);
        return new RestoreResultDto(newCollections.Count, newSets.Count, newSchedules.Count);
    }

    private static void RequireUnique(IEnumerable<string> names, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names.Select(n => (n ?? "").Trim()))
            if (!seen.Add(name)) throw new DomainException($"{what}ซ้ำกันในไฟล์: \"{name}\"");
    }

    /// <summary>
    /// The start date of a restored schedule. A date further ahead than a new schedule may start is refused. One that is
    /// in the past is moved up to yesterday: a backup is restored later than it was made, and nothing before today is
    /// ever queued, so the schedule behaves the same (a Once schedule whose day has passed stays over).
    /// </summary>
    private static DateOnly ParseDate(string? text, int utcOffsetMinutes, DateTimeOffset now, string schedule)
    {
        var (min, max) = Schedule.StartDateRange(now, utcOffsetMinutes);
        if (string.IsNullOrWhiteSpace(text)) return Schedule.LocalDayOf(now, utcOffsetMinutes);
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw new DomainException($"วันที่เริ่มของตาราง \"{schedule}\" ไม่ถูกต้อง ใช้รูปแบบ ปปปป-ดด-วว");
        if (d > max) throw new DomainException($"ตาราง \"{schedule}\": {Schedule.StartDateMessage(min, max)}");
        return d < min ? min : d;
    }

    /// <summary>A link's address in the file becomes the key of the link made from it; an account key stays when the set has the account.</summary>
    private static Dictionary<string, IReadOnlyList<string>> TranslateOverrides(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? overrides, LinkSet set, IReadOnlyList<SetLink> links)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (key, times) in overrides ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var k = (key ?? "").Trim();
            if (k.StartsWith("account:", StringComparison.OrdinalIgnoreCase))
            {
                if (Guid.TryParse(k["account:".Length..], out var accountId) && set.AccountIds.Contains(accountId))
                    result[Schedule.AccountKey(accountId)] = times;
                continue;
            }
            var url = FacebookGroupUrl.Normalize(k) ?? k;
            if (links.FirstOrDefault(l => FacebookGroupUrl.Same(l.Url, url)) is { } link) result[Schedule.LinkKey(link.Id)] = times;
        }
        return result;
    }

    private static NotificationSettings BuildNotifications(
        NotificationSettings current, BackupNotificationsDto rules, Dictionary<string, LinkSet> sets, Dictionary<Guid, List<SetLink>> linksOfSet)
    {
        // The channels' tokens and targets stay as they are; only where and which events go is restored.
        var result = new NotificationSettings
        {
            Telegram = current.Telegram,
            Line = current.Line,
            CommandsOn = current.CommandsOn,
            CommandsUsers = current.CommandsUsers,
            Channel = rules.Channel,
            Events = rules.Events.ToSettings(),
        };
        foreach (var rule in rules.Sets ?? [])
        {
            if (!sets.TryGetValue((rule.LinkSet ?? "").Trim(), out var set))
                throw new DomainException($"กฎแจ้งเตือนอ้างถึงชุดลิงก์ \"{rule.LinkSet}\" ที่ไม่มีในไฟล์");
            var own = new SetNotifyRule { Channel = rule.Channel, Events = rule.Events?.ToSettings() };
            foreach (var group in rule.Groups ?? [])
            {
                var url = FacebookGroupUrl.Normalize(group.Url) ?? (group.Url ?? "").Trim();
                if (linksOfSet[set.Id].FirstOrDefault(l => FacebookGroupUrl.Same(l.Url, url)) is { } link)
                    own.Groups[link.Id] = new GroupNotifyRule { Channel = group.Channel, Events = group.Events?.ToSettings() };
            }
            result.Sets[set.Id] = own;
        }
        return result;
    }

    private static AutoReplySettings BuildAutoReply(BackupAutoReplyDto reply, Dictionary<string, PostCollection> collections)
    {
        var result = new AutoReplySettings { On = reply.On };
        foreach (var rule in reply.Rules ?? [])
        {
            var scope = "all";
            if (!string.IsNullOrWhiteSpace(rule.Collection))
            {
                if (!collections.TryGetValue(rule.Collection.Trim(), out var collection))
                    throw new DomainException($"กฎตอบอัตโนมัติอ้างถึงชุดโพสต์ \"{rule.Collection}\" ที่ไม่มีในไฟล์");
                scope = collection.Id.ToString();
            }
            result.Rules.Add(new AutoReplyRule { Id = Guid.NewGuid(), Keywords = rule.Keywords, Reply = rule.Reply, Inbox = rule.Inbox, Scope = scope, On = rule.On });
        }
        return result;
    }
}

internal static class CollectionSettingsExtensions
{
    /// <summary>The same settings with approval switched on.</summary>
    public static CollectionSettings WithApproval(this CollectionSettings s)
    {
        s.RequireApproval = true;
        return s;
    }
}
