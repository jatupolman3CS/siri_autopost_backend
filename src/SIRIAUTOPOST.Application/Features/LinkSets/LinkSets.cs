using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Application.Features.LinkSets;

// Link sets ("ชุดลิงก์กลุ่ม"): Facebook group addresses with a group code and a daily cap, posted by one browser.
// Viewers read, editors write.

internal static class LinkSetLookups
{
    public static async Task<LinkSet> RequireAsync(ILinkSetRepository sets, Guid workspaceId, Guid id, CancellationToken ct) =>
        await sets.GetAsync(workspaceId, id, ct) ?? throw new NotFoundException("ชุดลิงก์", id);

    public static async Task<SetLink> RequireLinkAsync(ISetLinkRepository links, Guid workspaceId, Guid setId, Guid id, CancellationToken ct)
    {
        var link = await links.GetAsync(workspaceId, id, ct);
        return link is null || link.LinkSetId != setId ? throw new NotFoundException("ลิงก์กลุ่ม", id) : link;
    }

    public static async Task<LinkSetDto> ViewAsync(
        LinkSet set, ISetLinkRepository links, IScheduleRepository schedules, CancellationToken ct) =>
        LinkSetDto.From(set, await links.ListBySetAsync(set.WorkspaceId, set.Id, ct), (await schedules.ListByLinkSetAsync(set.WorkspaceId, set.Id, ct)).Count);

    /// <summary>Which extension holds which group in this workspace (see <see cref="OwnershipMap"/>).</summary>
    public static async Task<OwnershipMap> OwnershipAsync(
        ILinkSetRepository sets, ISetLinkRepository links, IReadOnlyList<SocialAccount> accounts, Guid workspaceId, CancellationToken ct) =>
        OwnershipMap.Build(await sets.ListAsync(workspaceId, ct), await links.ListAsync(workspaceId, ct), accounts);

    /// <summary>
    /// Moving a set to another extension is refused while one of its groups is in a set of a third extension: the
    /// groups of the set would then be posted by two machines.
    /// </summary>
    public static void EnsureSetFree(OwnershipMap map, LinkSet set, IEnumerable<SetLink> setLinks, Guid owner, IReadOnlyList<SocialAccount> accounts)
    {
        var clashes = setLinks.Where(l => l.IsUsable).Select(l => (Link: l, Held: map.ConflictFor(l.Url, owner, set.Id))).Where(x => x.Held is not null).ToList();
        if (clashes.Count == 0) return;
        var more = clashes.Count > 1 ? $" (และอีก {clashes.Count - 1} กลุ่ม)" : "";
        throw new DomainException(OwnershipMap.Refusal(clashes[0].Link.Url, clashes[0].Held!, accounts) + more);
    }

    /// <summary>A group that is about to be posted from this set must not be in a set of another extension.</summary>
    public static async Task EnsureLinkFreeAsync(
        ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts, LinkSet set, string url, CancellationToken ct)
    {
        var accountList = await accounts.ListAsync(set.WorkspaceId, ct);
        if (LinkOwnership.OwnerOf(set, accountList) is not { } owner) return; // nobody posts this set yet: the extension chosen later is checked then
        (await OwnershipAsync(sets, links, accountList, set.WorkspaceId, ct)).EnsureFree(url, owner, set.Id, accountList);
    }

    /// <summary>The next position for a link at the end of the set.</summary>
    public static async Task<int> NextLinkOrderAsync(ISetLinkRepository links, Guid setId, CancellationToken ct) =>
        await links.MaxSortOrderAsync(setId, ct) + 1;

    public static void EnsureRoomForLinks(int existing, int adding)
    {
        if (existing + adding > LinkSet.MaxLinks) throw new DomainException($"ชุดลิงก์เก็บได้ไม่เกิน {LinkSet.MaxLinks} ลิงก์ต่อชุด");
    }

    /// <summary>Announces a link that changed health, when a browser posts the set (otherwise there is nobody to tell).</summary>
    public static async Task AnnounceAsync(
        LinkSet set, SetLink link, IAccountRepository accounts, IDeviceEventRepository events, DateTimeOffset now, CancellationToken ct)
    {
        if (await LinkSetAccounts.EventDeviceAsync(set, accounts, ct) is { } device)
            events.Add(DeviceEvents.LinksChanged(set.WorkspaceId, device, set.Id, link.Id, link.Health, now));
    }
}

public sealed record GetLinkSetsQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<LinkSetDto>>;

public sealed class GetLinkSetsQueryHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IScheduleRepository schedules, ICurrentUser current)
    : IQueryHandler<GetLinkSetsQuery, IReadOnlyList<LinkSetDto>>
{
    public async Task<IReadOnlyList<LinkSetDto>> HandleAsync(GetLinkSetsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var all = await sets.ListAsync(q.WorkspaceId, ct);
        var byLinkSet = (await links.ListAsync(q.WorkspaceId, ct)).ToLookup(l => l.LinkSetId);
        var used = (await schedules.ListAsync(q.WorkspaceId, ct)).ToLookup(s => s.LinkSetId);
        return all.Select(s => LinkSetDto.From(s, byLinkSet[s.Id].ToList(), used[s.Id].Count())).ToList();
    }
}

public sealed record CreateLinkSetCommand(Guid WorkspaceId, string Name, Guid? PostAsAccountId) : ICommand<LinkSetDto>;

public sealed class CreateLinkSetCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, IAccountRepository accounts, ISetLinkRepository links,
    IScheduleRepository schedules, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateLinkSetCommand, LinkSetDto>
{
    public async Task<LinkSetDto> HandleAsync(CreateLinkSetCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        if (await sets.CountAsync(c.WorkspaceId, ct) >= LinkSet.MaxPerWorkspace)
            throw new DomainException($"สร้างชุดลิงก์ได้ไม่เกิน {LinkSet.MaxPerWorkspace} ชุดต่อเวิร์กสเปซ");
        var accountList = await accounts.ListAsync(c.WorkspaceId, ct);
        LinkSetAccounts.EnsureValid(c.PostAsAccountId, [], accountList);
        // A set belongs to one extension from the start: the only connected one is named for the person, and with
        // several the person has to choose (no "first one" guess that sends this set's groups to the wrong machine).
        var postAs = c.PostAsAccountId ?? LinkOwnership.OnlyConnected(accountList)?.Id;
        if (postAs is null && accountList.Count(a => a.Platform == Platform.Fb && a.IsConnected) >= 2)
            throw new DomainException(LinkSetAccounts.NoChoice);
        var set = LinkSet.Create(c.WorkspaceId, c.Name, postAs, clock.GetUtcNow(), await sets.MaxSortOrderAsync(c.WorkspaceId, ct) + 1);
        sets.Add(set);
        await uow.SaveChangesAsync(ct);
        return await LinkSetLookups.ViewAsync(set, links, schedules, ct);
    }
}

/// <summary>
/// Renames a set and says which account posts it and which others do. When that changes, the posts its schedules already
/// queued for the future are removed (or, for an account that was only added, the schedules fill in what is missing).
/// </summary>
public sealed record UpdateLinkSetCommand(
    Guid WorkspaceId, Guid LinkSetId, string Name, Guid? PostAsAccountId, IReadOnlyList<Guid>? AccountIds, bool? RandomOrder = null, int? DailyPerGroup = null)
    : ICommand<LinkSetDto>;

public sealed class UpdateLinkSetCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, IAccountRepository accounts, ISetLinkRepository links,
    IScheduleRepository schedules, ScheduleSync sync, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateLinkSetCommand, LinkSetDto>
{
    public async Task<LinkSetDto> HandleAsync(UpdateLinkSetCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var accountIds = (c.AccountIds ?? []).Distinct().ToList();
        // One set, one extension: a set cannot post through other accounts' extensions any more.
        if (accountIds.Count > 0) throw new DomainException(LinkSetAccounts.OtherAccountsRetired);
        var accountList = await accounts.ListAsync(c.WorkspaceId, ct);
        LinkSetAccounts.EnsureValid(c.PostAsAccountId, accountIds, accountList);
        var (poster, others) = (set.PostAsAccountId, set.AccountIds.ToList());
        var (order, perGroup) = (set.RandomOrder, set.DailyPerGroup);
        set.Update(c.Name, c.PostAsAccountId, accountIds, c.RandomOrder, c.DailyPerGroup);
        // One set, one extension (2026-10-10): "no choice" is only possible while a single extension is connected, and
        // handing the set to another extension is refused while one of its groups belongs to a third.
        if (set.PostAsAccountId is null && accountList.Count(a => a.Platform == Platform.Fb && a.IsConnected) >= 2)
            throw new DomainException(LinkSetAccounts.NoChoice);
        if (poster != set.PostAsAccountId && LinkOwnership.OwnerOf(set, accountList) is { } owner)
            LinkSetLookups.EnsureSetFree(
                await LinkSetLookups.OwnershipAsync(sets, links, accountList, c.WorkspaceId, ct), set,
                await links.ListBySetAsync(c.WorkspaceId, set.Id, ct), owner, accountList);
        var removed = others.Except(set.AccountIds).Any();
        var added = set.AccountIds.Except(others).Any();
        // The order and the daily limit are decided when the posts are queued: the ones already queued belong to the old rules.
        var rules = order != set.RandomOrder || perGroup != set.DailyPerGroup;
        if (poster != set.PostAsAccountId || removed || rules) await sync.SetChangedAsync(c.WorkspaceId, set.Id, dropQueued: true, clock.GetUtcNow(), ct);
        else if (added) await sync.SetChangedAsync(c.WorkspaceId, set.Id, dropQueued: false, clock.GetUtcNow(), ct);
        await uow.SaveChangesAsync(ct);
        return await LinkSetLookups.ViewAsync(set, links, schedules, ct);
    }
}

/// <summary>
/// Switches a link set on or off. Off: the schedules that use it queue nothing to it and the posts they still had queued
/// for the future are removed. On: those schedules fill in what is missing.
/// </summary>
public sealed record SetLinkSetActiveCommand(Guid WorkspaceId, Guid LinkSetId, bool Active) : ICommand<LinkSetDto>;

public sealed class SetLinkSetActiveCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IScheduleRepository schedules,
    ScheduleSync sync, ScheduleTopUp topUp, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetLinkSetActiveCommand, LinkSetDto>
{
    public async Task<LinkSetDto> HandleAsync(SetLinkSetActiveCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        if (set.Active != c.Active)
        {
            var used = await schedules.ListByLinkSetAsync(c.WorkspaceId, c.LinkSetId, ct);
            set.SetActive(c.Active);
            await sync.SourceToggledAsync(used, c.Active, clock.GetUtcNow(), ct);
            await uow.SaveChangesAsync(ct);
            if (c.Active) await SourceTopUp.RunAsync(topUp, c.WorkspaceId, used, ct);
        }
        return await LinkSetLookups.ViewAsync(set, links, schedules, ct);
    }
}

/// <summary>Deletes a link set and its links. Refused while a schedule uses it.</summary>
public sealed record DeleteLinkSetCommand(Guid WorkspaceId, Guid LinkSetId) : ICommand<Unit>;

public sealed class DeleteLinkSetCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, IScheduleRepository schedules, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeleteLinkSetCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteLinkSetCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var used = await schedules.ListByLinkSetAsync(c.WorkspaceId, c.LinkSetId, ct);
        if (used.Count > 0)
            throw new DomainException($"ลบชุดลิงก์นี้ไม่ได้ เพราะยังใช้อยู่ในตาราง: {string.Join(", ", used.Select(s => s.Name))}");
        sets.Remove(set);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Adds a link. A blank or invalid address is allowed: the row is created invalid so the web app can add an empty row.</summary>
public sealed record AddLinkCommand(Guid WorkspaceId, Guid LinkSetId, string? Name, string? Url, string? Code, int? DailyMax) : ICommand<SetLinkDto>;

public sealed class AddLinkCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts, ScheduleSync sync,
    PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<AddLinkCommand, SetLinkDto>
{
    public async Task<SetLinkDto> HandleAsync(AddLinkCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var existing = await links.ListBySetAsync(c.WorkspaceId, set.Id, ct);
        LinkSetLookups.EnsureRoomForLinks(existing.Count, 1);
        await quotas.EnsureGroupsAsync(c.WorkspaceId, 1, ct);
        var link = SetLink.Create(c.WorkspaceId, set.Id, c.Name, c.Url, c.Code, c.DailyMax ?? 0, clock.GetUtcNow(),
            existing.Count == 0 ? 0 : existing.Max(l => l.SortOrder) + 1);
        if (link.IsUsable) await LinkSetLookups.EnsureLinkFreeAsync(sets, links, accounts, set, link.Url, ct); // one group, one extension
        links.Add(link);
        if (link.IsUsable) await sync.LinksAddedAsync(c.WorkspaceId, set.Id, ct); // the set's schedules make its posts at the next top-up
        await uow.SaveChangesAsync(ct);
        return SetLinkDto.From(link, link.IsValid && existing.Any(l => FacebookGroupUrl.Same(l.Url, link.Url)));
    }
}

public sealed record UpdateLinkCommand(
    Guid WorkspaceId, Guid LinkSetId, Guid LinkId, string? Name, string? Url, string? Code, int DailyMax, bool Enabled) : ICommand<SetLinkDto>;

/// <summary>
/// Edits a link. A link that is switched off, or posts to another address or with another code or daily limit, loses the posts its
/// schedules queued for the future (they would go the old way); one that is switched on or newly usable gets its posts at
/// the next top-up.
/// </summary>
public sealed class UpdateLinkCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts, ScheduleSync sync,
    IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateLinkCommand, SetLinkDto>
{
    public async Task<SetLinkDto> HandleAsync(UpdateLinkCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var link = await LinkSetLookups.RequireLinkAsync(links, c.WorkspaceId, set.Id, c.LinkId, ct);
        var (enabled, health) = (link.Enabled, link.Health);
        var (usable, url, code, dailyMax) = (link.IsUsable, link.Url, link.Code, link.DailyMax);
        link.Edit(c.Name, c.Url, c.Code, c.DailyMax, c.Enabled);
        // A link that starts to post (new address, switched on) must not hold a group another extension already has.
        if (link.IsUsable && (!usable || link.Url != url))
            await LinkSetLookups.EnsureLinkFreeAsync(sets, links, accounts, set, link.Url, ct);
        var now = clock.GetUtcNow();
        if (link.Enabled != enabled || link.Health != health) await LinkSetLookups.AnnounceAsync(set, link, accounts, events, now, ct);
        // The daily limit decides which of a day's slots the group gets, and that is chosen when the posts are queued: the
        // posts still to come are made again under the new limit.
        var moved = link.Url != url || link.Code != code || link.DailyMax != dailyMax;
        if (usable && (!link.IsUsable || moved)) await sync.LinksChangedAsync(c.WorkspaceId, set.Id, [link.Id], now, ct);
        else if (link.IsUsable && !usable) await sync.LinksAddedAsync(c.WorkspaceId, set.Id, ct);
        await uow.SaveChangesAsync(ct);
        var others = await links.ListBySetAsync(c.WorkspaceId, set.Id, ct);
        return SetLinkDto.From(link, link.IsValid && others.TakeWhile(l => l.Id != link.Id).Any(l => FacebookGroupUrl.Same(l.Url, link.Url)));
    }
}

public sealed record DeleteLinkCommand(Guid WorkspaceId, Guid LinkSetId, Guid LinkId) : ICommand<Unit>;

public sealed class DeleteLinkCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, ScheduleSync sync, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeleteLinkCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteLinkCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var link = await LinkSetLookups.RequireLinkAsync(links, c.WorkspaceId, set.Id, c.LinkId, ct);
        links.Remove(link);
        await sync.LinksChangedAsync(c.WorkspaceId, set.Id, [link.Id], clock.GetUtcNow(), ct); // its queued posts go with it
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>"Enable again" after the engine switched a link off.</summary>
public sealed record EnableLinkCommand(Guid WorkspaceId, Guid LinkSetId, Guid LinkId) : ICommand<SetLinkDto>;

public sealed class EnableLinkCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts, ScheduleSync sync,
    IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<EnableLinkCommand, SetLinkDto>
{
    public async Task<SetLinkDto> HandleAsync(EnableLinkCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var link = await LinkSetLookups.RequireLinkAsync(links, c.WorkspaceId, set.Id, c.LinkId, ct);
        link.Enable();
        if (link.IsUsable) await LinkSetLookups.EnsureLinkFreeAsync(sets, links, accounts, set, link.Url, ct);
        await LinkSetLookups.AnnounceAsync(set, link, accounts, events, clock.GetUtcNow(), ct);
        if (link.IsUsable) await sync.LinksAddedAsync(c.WorkspaceId, set.Id, ct); // back in the set: its schedules make its posts again
        await uow.SaveChangesAsync(ct);
        var others = await links.ListBySetAsync(c.WorkspaceId, set.Id, ct);
        return SetLinkDto.From(link, link.IsValid && others.TakeWhile(l => l.Id != link.Id).Any(l => FacebookGroupUrl.Same(l.Url, link.Url)));
    }
}

/// <summary>
/// Pasted lines "address | code". Blank lines are skipped, an address that is not a Facebook group counts as invalid, an
/// address already in the set counts as a duplicate (and its code is updated when another non-empty code is given).
/// </summary>
public sealed record BulkAddLinksCommand(Guid WorkspaceId, Guid LinkSetId, string Text) : ICommand<BulkLinksResultDto>;

public sealed class BulkAddLinksCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts, IScheduleRepository schedules,
    ScheduleSync sync, PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<BulkAddLinksCommand, BulkLinksResultDto>
{
    public async Task<BulkLinksResultDto> HandleAsync(BulkAddLinksCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var now = clock.GetUtcNow();
        var existing = (await links.ListBySetAsync(c.WorkspaceId, set.Id, ct)).ToList();
        var byUrl = new Dictionary<string, SetLink>(FacebookGroupUrl.Comparer);
        foreach (var l in existing) byUrl.TryAdd(l.Url, l);
        // One group, one extension: an address another extension's set already holds is left out and reported.
        var accountList = await accounts.ListAsync(c.WorkspaceId, ct);
        var owner = LinkOwnership.OwnerOf(set, accountList);
        var map = owner is null ? null : await LinkSetLookups.OwnershipAsync(sets, links, accountList, c.WorkspaceId, ct);
        var (conflicts, conflictNote) = (0, (string?)null);

        var (added, duplicates, recoded, invalid) = (0, 0, 0, 0);
        var order = existing.Count == 0 ? 0 : existing.Max(l => l.SortOrder) + 1;
        var created = new List<SetLink>();
        var recodedLinks = new List<Guid>();
        foreach (var raw in c.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var url = FacebookGroupUrl.Normalize(parts[0]);
            var code = parts.Length > 1 ? parts[1] : "";
            if (url is null || !SetLink.Fits(url, code)) { invalid++; continue; }
            if (byUrl.TryGetValue(url, out var old))
            {
                duplicates++;
                if (code.Length > 0 && code != old.Code)
                {
                    old.Edit(old.Name, old.Url, code, old.DailyMax, old.Enabled);
                    recodedLinks.Add(old.Id);
                    recoded++;
                }
                continue;
            }
            if (map?.ConflictFor(url, owner!.Value, set.Id) is { } held)
            {
                conflicts++;
                conflictNote ??= OwnershipMap.Refusal(url, held, accountList);
                continue;
            }
            var link = SetLink.Create(c.WorkspaceId, set.Id, null, url, code, 0, now, order++);
            byUrl[url] = link;
            created.Add(link);
            added++;
        }
        LinkSetLookups.EnsureRoomForLinks(existing.Count, created.Count);
        await quotas.EnsureGroupsAsync(c.WorkspaceId, created.Count, ct);
        links.AddRange(created);
        // A link with a new code posts differently from now on; new links are posted to at the next top-up.
        if (recodedLinks.Count > 0) await sync.LinksChangedAsync(c.WorkspaceId, set.Id, recodedLinks, now, ct);
        else if (created.Count > 0) await sync.LinksAddedAsync(c.WorkspaceId, set.Id, ct);
        await uow.SaveChangesAsync(ct);
        return new BulkLinksResultDto(added, duplicates, recoded, invalid, await LinkSetLookups.ViewAsync(set, links, schedules, ct), conflicts, conflictNote);
    }
}

public sealed record GetAccountGroupsQuery(Guid WorkspaceId, Guid AccountId) : IQuery<IReadOnlyList<GroupLinkDto>>;

/// <summary>The groups a browser synced for its account: what the "import groups" window lists.</summary>
public sealed class GetAccountGroupsQueryHandler(IWorkspaceRepository workspaces, IAccountRepository accounts, ICurrentUser current)
    : IQueryHandler<GetAccountGroupsQuery, IReadOnlyList<GroupLinkDto>>
{
    public async Task<IReadOnlyList<GroupLinkDto>> HandleAsync(GetAccountGroupsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var account = await accounts.GetAsync(q.WorkspaceId, q.AccountId, ct) ?? throw new NotFoundException("บัญชี", q.AccountId);
        return account.GroupLinks.Select(g => new GroupLinkDto(g.Name, g.Url)).ToList();
    }
}

/// <summary>Adds groups of a connected account to a set, skipping the ones it already has.</summary>
public sealed record ImportAccountGroupsCommand(Guid WorkspaceId, Guid LinkSetId, Guid AccountId, IReadOnlyList<string> Urls) : ICommand<LinkSetDto>;

public sealed class ImportAccountGroupsCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts,
    IScheduleRepository schedules, ScheduleSync sync, PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ImportAccountGroupsCommand, LinkSetDto>
{
    public async Task<LinkSetDto> HandleAsync(ImportAccountGroupsCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var set = await LinkSetLookups.RequireAsync(sets, c.WorkspaceId, c.LinkSetId, ct);
        var account = await accounts.GetAsync(c.WorkspaceId, c.AccountId, ct) ?? throw new NotFoundException("บัญชี", c.AccountId);
        var groups = new Dictionary<string, GroupLink>(FacebookGroupUrl.Comparer);
        foreach (var g in account.GroupLinks)
            if (FacebookGroupUrl.Normalize(g.Url) is { } url) groups.TryAdd(url, g);

        var existing = await links.ListBySetAsync(c.WorkspaceId, set.Id, ct);
        var have = existing.Select(l => l.Url).ToHashSet(FacebookGroupUrl.Comparer);
        var now = clock.GetUtcNow();
        var order = existing.Count == 0 ? 0 : existing.Max(l => l.SortOrder) + 1;
        var created = new List<SetLink>();
        // One group, one extension: a group another extension's set already holds is refused (naming it), not skipped quietly.
        var accountList = await accounts.ListAsync(c.WorkspaceId, ct);
        var owner = LinkOwnership.OwnerOf(set, accountList);
        var map = owner is null ? null : await LinkSetLookups.OwnershipAsync(sets, links, accountList, c.WorkspaceId, ct);
        foreach (var pick in c.Urls.Select(u => FacebookGroupUrl.Normalize(u)))
        {
            if (pick is null || !SetLink.Fits(pick, null) || !groups.TryGetValue(pick, out var group) || !have.Add(pick)) continue;
            map?.EnsureFree(pick, owner, set.Id, accountList);
            // The extension names a group by its address when it has no name: let the address give the name instead.
            var name = group.Name == group.Url || group.Name.Length > SetLink.MaxNameLength ? null : group.Name;
            created.Add(SetLink.Create(c.WorkspaceId, set.Id, name, pick, null, 0, now, order++));
        }
        LinkSetLookups.EnsureRoomForLinks(existing.Count, created.Count);
        await quotas.EnsureGroupsAsync(c.WorkspaceId, created.Count, ct);
        links.AddRange(created);
        if (created.Count > 0) await sync.LinksAddedAsync(c.WorkspaceId, set.Id, ct);
        await uow.SaveChangesAsync(ct);
        return await LinkSetLookups.ViewAsync(set, links, schedules, ct);
    }
}

/// <summary>Rows "set, name, address, code": sets are created by name when missing; duplicates and invalid rows are skipped.</summary>
public sealed record ImportLinksCsvCommand(Guid WorkspaceId, IReadOnlyList<CsvLinkRow> Rows) : ICommand<CsvImportResultDto>;

public sealed class ImportLinksCsvCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository sets, ISetLinkRepository links, IAccountRepository accounts, ScheduleSync sync,
    PlanQuotas quotas, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ImportLinksCsvCommand, CsvImportResultDto>
{
    public const int MaxRows = 5000;

    public async Task<CsvImportResultDto> HandleAsync(ImportLinksCsvCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var now = clock.GetUtcNow();
        var all = (await sets.ListAsync(c.WorkspaceId, ct)).ToList();
        var byName = new Dictionary<string, LinkSet>();
        foreach (var s in all) byName.TryAdd(s.Name, s);
        var everyLink = await links.ListAsync(c.WorkspaceId, ct);
        var existingLinks = everyLink.GroupBy(l => l.LinkSetId).ToList();
        // One group, one extension: a row whose group another extension's set holds (or an earlier row of this file gave to
        // another extension) is left out and counted. A set made here goes to the only connected extension, else to nobody
        // until a person picks one.
        var accountList = await accounts.ListAsync(c.WorkspaceId, ct);
        var map = OwnershipMap.Build(all, everyLink, accountList);
        var onlyConnected = LinkOwnership.OnlyConnected(accountList)?.Id;
        var givenTo = new Dictionary<string, GroupHolder>(FacebookGroupUrl.Comparer);
        var (conflicts, conflictNote) = (0, (string?)null);
        var urlsOf = existingLinks.ToDictionary(g => g.Key, g => g.Select(l => l.Url).ToHashSet(FacebookGroupUrl.Comparer));
        var orders = existingLinks.ToDictionary(g => g.Key, g => g.Max(l => l.SortOrder) + 1);
        var nextSet = await sets.MaxSortOrderAsync(c.WorkspaceId, ct) + 1;

        var (addedLinks, createdSets, invalid) = (0, 0, 0);
        var newLinks = new List<SetLink>();
        foreach (var row in c.Rows)
        {
            var setName = (row.Set ?? "").Trim();
            var url = FacebookGroupUrl.Normalize(row.Url);
            var name = (row.Name ?? "").Trim();
            if (setName.Length == 0 || setName.Length > LinkSet.MaxNameLength || url is null || !SetLink.Fits(url, row.Code) || name.Length > SetLink.MaxNameLength)
            {
                invalid++;
                continue;
            }
            if (!byName.TryGetValue(setName, out var set))
            {
                set = LinkSet.Create(c.WorkspaceId, setName, onlyConnected, now, nextSet++);
                sets.Add(set);
                byName[setName] = set;
                createdSets++;
            }
            var urls = urlsOf.TryGetValue(set.Id, out var u) ? u : urlsOf[set.Id] = new HashSet<string>(FacebookGroupUrl.Comparer);
            if (urls.Contains(url)) continue; // already in the set: skipped, not an error
            var owner = LinkOwnership.OwnerOf(set, accountList);
            if (owner is { } o && (map.ConflictFor(url, o, set.Id) ?? (givenTo.TryGetValue(url, out var earlier) && earlier.Owner != o ? earlier : null)) is { } held)
            {
                conflicts++;
                conflictNote ??= OwnershipMap.Refusal(url, held, accountList);
                continue;
            }
            urls.Add(url);
            var order = orders.GetValueOrDefault(set.Id);
            orders[set.Id] = order + 1;
            var newLink = SetLink.Create(c.WorkspaceId, set.Id, name, url, row.Code, 0, now, order);
            newLinks.Add(newLink);
            if (owner is { } given && newLink.IsUsable) givenTo.TryAdd(url, new GroupHolder(set, newLink, given));
            addedLinks++;
        }
        if (all.Count + createdSets > LinkSet.MaxPerWorkspace)
            throw new DomainException($"สร้างชุดลิงก์ได้ไม่เกิน {LinkSet.MaxPerWorkspace} ชุดต่อเวิร์กสเปซ");
        foreach (var g in newLinks.GroupBy(l => l.LinkSetId))
            LinkSetLookups.EnsureRoomForLinks(urlsOf[g.Key].Count - g.Count(), g.Count());
        await quotas.EnsureGroupsAsync(c.WorkspaceId, newLinks.Count, ct);
        links.AddRange(newLinks);
        foreach (var setId in newLinks.Select(l => l.LinkSetId).Distinct()) await sync.LinksAddedAsync(c.WorkspaceId, setId, ct);
        await uow.SaveChangesAsync(ct);
        return new CsvImportResultDto(addedLinks, createdSets, invalid, conflicts, conflictNote);
    }
}
