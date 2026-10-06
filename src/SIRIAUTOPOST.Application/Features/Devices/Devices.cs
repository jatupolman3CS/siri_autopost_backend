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

namespace SIRIAUTOPOST.Application.Features.Devices;

// ---------- owner side (web app, JWT) ----------

public sealed record GetDevicesQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<DeviceDto>>;

public sealed class GetDevicesQueryHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetDevicesQuery, IReadOnlyList<DeviceDto>>
{
    public async Task<IReadOnlyList<DeviceDto>> HandleAsync(GetDevicesQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var byDevice = (await accounts.ListAsync(ws.Id, ct))
            .Where(a => a.DeviceId is not null)
            .ToDictionary(a => a.DeviceId!.Value, a => a.Id);
        var now = clock.GetUtcNow();
        return (await devices.ListAsync(ws.Id, ct))
            .Select(d => DeviceDto.From(d, byDevice.TryGetValue(d.Id, out var acc) ? acc : null, now))
            .ToList();
    }
}

/// <summary>A code to type into the extension. Refused when the owner's plan has no device left.</summary>
public sealed record CreatePairingCodeCommand(Guid WorkspaceId) : ICommand<PairingCodeDto>;

public sealed class CreatePairingCodeCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IPlanRepository plans, IDeviceRepository devices,
    IAccountRepository accounts, IDevicePairingRepository pairings, IDeviceSecrets secrets, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreatePairingCodeCommand, PairingCodeDto>
{
    public async Task<PairingCodeDto> HandleAsync(CreatePairingCodeCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var max = await DeviceLimit.EnsureRoomAsync(ws, users, plans, devices, accounts, workspaces, ct);
        var now = clock.GetUtcNow();
        await pairings.DeleteExpiredAsync(now.AddDays(-1), ct); // a day-old code is no use to anyone
        var pairing = DevicePairing.Create(ws.Id, secrets.NewPairingCode(), now);
        pairings.Add(pairing);
        await uow.SaveChangesAsync(ct);
        return new PairingCodeDto(pairing.Code, pairing.ExpiresAt, max);
    }
}

/// <summary>
/// Unbinds a device. Its account keeps its history but needs a new pairing to post again, so the posts it
/// still had to send are failed (they would never be claimed: the new pairing makes a new account).
/// </summary>
public sealed record RevokeDeviceCommand(Guid WorkspaceId, Guid DeviceId) : ICommand<Unit>;

public sealed class RevokeDeviceCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, IPostRepository posts, IPostBumpRepository bumps,
    IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RevokeDeviceCommand, Unit>
{
    public async Task<Unit> HandleAsync(RevokeDeviceCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        await DeviceRevocation.RevokeAsync(device, "ยกเลิกการผูกอุปกรณ์ระหว่างโพสต์", accounts, posts, bumps, devices, events, clock.GetUtcNow(), ct);
        await uow.SaveChangesAsync(ct);
        await events.PruneAsync(device.Id, DeviceRevocation.KeepEvents, ct);
        return Unit.Value;
    }
}

/// <summary>What unbinding a device does, shared by the owner's team and the platform admin.</summary>
internal static class DeviceRevocation
{
    /// <summary>Events kept for a device that is gone: nothing prunes them later.</summary>
    public const int KeepEvents = 50;

    public const string QueuedDetail = "อุปกรณ์ถูกยกเลิกการผูกแล้ว ต้องจับคู่เครื่องใหม่ก่อนจึงจะโพสต์ได้";

    /// <summary>Disconnects the account, fails its unfinished posts and removes the device. The caller saves.</summary>
    public static async Task RevokeAsync(
        Device device, string whilePostingDetail, IAccountRepository accounts, IPostRepository posts, IPostBumpRepository bumps,
        IDeviceRepository devices, IDeviceEventRepository events, DateTimeOffset now, CancellationToken ct)
    {
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        account?.Disconnect();
        foreach (var p in await posts.ListClaimedByAsync(device.Id, ct))
        {
            p.Fail(FailureCode.Network, whilePostingDetail, now);
            events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
        }
        if (account is not null)
        {
            foreach (var p in await posts.ListOpenByAccountAsync(account.Id, ct))
            {
                p.Fail(FailureCode.Session, QueuedDetail, now);
                events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
            }
            // Comments that were to bump its posts have no browser to make them any more.
            foreach (var b in await bumps.ListOpenByAccountAsync(account.Id, ct)) b.Skip(QueuedDetail, now);
        }
        devices.Remove(device);
        events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Revoked, new { name = device.Name }, now));
    }
}

/// <summary>
/// The web app's controls of a paired browser: its name (and its Facebook account's) and whether it takes
/// posts scheduled on the web. null leaves a value as it is.
/// </summary>
public sealed record UpdateDeviceCommand(Guid WorkspaceId, Guid DeviceId, string? Name, bool? JobsPaused) : ICommand<DeviceDto>;

public sealed class UpdateDeviceCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, IDeviceEventRepository events,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateDeviceCommand, DeviceDto>
{
    public async Task<DeviceDto> HandleAsync(UpdateDeviceCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        if (c.Name is not null)
        {
            // Extensions of one workspace must have different names: they are told apart by name when posting.
            var wanted = c.Name.Trim();
            if (wanted.Length > 0 && (await devices.ListNamesAsync(ws.Id, device.Id, ct)).Any(n => Device.SameName(n, wanted)))
                throw new DomainException($"มีส่วนขยายชื่อ \"{wanted}\" อยู่ในบัญชีนี้แล้ว ตั้งชื่อที่ไม่ซ้ำกัน");
            device.Rename(c.Name);
            account?.FollowDevice(device);
        }
        var now = clock.GetUtcNow();
        if (c.JobsPaused is { } paused)
        {
            device.SetJobsPaused(paused);
            if (!paused) device.EndAutoPause(now, force: true); // "resume" also lifts the engine's own pause
        }
        events.Add(DevicePause.Changed(device, now));
        await uow.SaveChangesAsync(ct);
        return DeviceDto.From(device, account?.Id, now);
    }
}

internal static class DeviceLimit
{
    /// <summary>
    /// Returns the owner's device limit, or throws when the workspace already has that many devices, or the owner
    /// already has as many connected accounts (all workspaces) as the plan allows: each device brings one.
    /// </summary>
    public static async Task<int?> EnsureRoomAsync(
        Workspace ws, IUserRepository users, IPlanRepository plans, IDeviceRepository devices, IAccountRepository accounts,
        IWorkspaceRepository workspaces, CancellationToken ct)
    {
        var owner = await users.OwnerOfAsync(ws, ct);
        var limits = await plans.ForAsync(owner, ct);
        if (limits.Devices is { } m && await devices.CountAsync(ws.Id, ct) >= m)
            throw new DomainException($"แผนปัจจุบันผูกอุปกรณ์ได้สูงสุด {m} เครื่อง ยกเลิกการผูกเครื่องเดิมหรืออัปเกรดแผนก่อน");
        if (limits.Accounts is { } a)
        {
            var own = (await workspaces.ListByOwnerAsync(owner.Id, ct)).Select(w => w.Id);
            if (await accounts.CountConnectedAsync(own, ct) >= a)
                throw new DomainException($"แผนปัจจุบันเชื่อมบัญชีได้สูงสุด {a} บัญชี อัปเกรดแผนก่อน");
        }
        return limits.Devices;
    }
}

// ---------- device side (extension, X-Device-Key) ----------

/// <summary>The extension trades a pairing code for its device key, and gets a Facebook account in the workspace.</summary>
public sealed record PairDeviceCommand(string Code, string Name, string? Browser, string? Version) : ICommand<PairResultDto>;

public sealed class PairDeviceCommandHandler(
    IDevicePairingRepository pairings, IWorkspaceRepository workspaces, IUserRepository users, IPlanRepository plans,
    IDeviceRepository devices, IAccountRepository accounts, IDeviceEventRepository events, IDeviceSecrets secrets, IUnitOfWork uow,
    TimeProvider clock)
    : ICommandHandler<PairDeviceCommand, PairResultDto>
{
    public async Task<PairResultDto> HandleAsync(PairDeviceCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var code = NormalizeCode(c.Code);
        var pairing = await pairings.GetByCodeAsync(code, ct) ?? throw new DomainException("ไม่พบรหัสจับคู่นี้ ตรวจสอบรหัสอีกครั้ง");
        pairing.Use(now);
        var ws = await workspaces.GetByIdAsync(pairing.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", pairing.WorkspaceId);
        await DeviceLimit.EnsureRoomAsync(ws, users, plans, devices, accounts, workspaces, ct);

        var key = secrets.NewDeviceKey();
        // A second browser that pairs with the same name ("Chrome 6/10/2569") gets "(2)": names are unique in a workspace.
        var name = Device.UniqueName(c.Name, await devices.ListNamesAsync(ws.Id, null, ct));
        var device = Device.Pair(ws.Id, name, c.Browser ?? "", c.Version ?? "", secrets.Hash(key), now);
        devices.Add(device);
        var account = SocialAccount.Connect(ws.Id, device, await accounts.CountAsync(ws.Id, ct));
        accounts.Add(account);
        events.Add(DeviceEvents.Make(ws.Id, device.Id, DeviceEventType.Paired, new { name = device.Name, accountId = account.Id }, now));
        await uow.SaveChangesAsync(ct);
        return new PairResultDto(key, device.Id, device.Name, ws.Id, ws.Name, account.Id);
    }

    /// <summary>"k7qf 2mxp" and "K7QF-2MXP" are the same code.</summary>
    public static string NormalizeCode(string code)
    {
        var raw = new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return raw.Length == 8 ? raw[..4] + "-" + raw[4..] : raw;
    }
}

public sealed record DeviceHeartbeatCommand(string? Version) : ICommand<DeviceStatusDto>;

public sealed class DeviceHeartbeatCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts,
    IUserRepository users, IDeviceEventRepository events, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeviceHeartbeatCommand, DeviceStatusDto>
{
    public async Task<DeviceStatusDto> HandleAsync(DeviceHeartbeatCommand c, CancellationToken ct = default)
    {
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        var now = clock.GetUtcNow();
        if (device.Seen(c.Version, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        DevicePause.Settle(device, events, now);
        await uow.SaveChangesAsync(ct);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        // "Paused" is also what a suspended, banned or paused customer's device is told, and a device the engine
        // paused itself (a Facebook block, posts that kept failing): it gets no jobs.
        var owner = await users.OwnerOfAsync(ws, ct);
        var auto = device.IsAutoPaused(now);
        return new DeviceStatusDto(device.Id, device.Name, ws.Id, ws.Name, account?.Id, account?.GroupLinks.Count ?? 0,
            ws.ExtensionOnline, AntiBanDto.From(ws.AntiBan), device.JobsPaused || !owner.CanPost || auto,
            auto ? device.AutoPausedUntil : null, auto ? device.AutoPauseReason : null);
    }
}

/// <summary>The groups set up in the extension become the groups of the device's Facebook account.</summary>
public sealed record SyncDeviceGroupsCommand(IReadOnlyList<GroupLinkDto> Groups) : ICommand<int>;

public sealed class SyncDeviceGroupsCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IAccountRepository accounts, IDeviceEventRepository events,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SyncDeviceGroupsCommand, int>
{
    public async Task<int> HandleAsync(SyncDeviceGroupsCommand c, CancellationToken ct = default)
    {
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        var now = clock.GetUtcNow();
        if (device.Seen(null, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        var account = await accounts.GetByDeviceAsync(device.Id, ct) ?? throw new NotFoundException("บัญชีของอุปกรณ์", device.Id);
        if (account.SyncGroups(c.Groups.Select(g => new GroupLink { Name = g.Name, Url = g.Url })))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Groups, new { count = account.GroupLinks.Count }, now));
        await uow.SaveChangesAsync(ct);
        return account.GroupLinks.Count;
    }
}

/// <summary>
/// Hands the device the next post to publish, or nothing. First it lets the schedules catch up with the clock
/// (<see cref="ScheduleTopUp"/>). On the way it settles posts it cannot hand out: too late for the offline policy
/// (skipped), for a link that was switched off or deleted or whose daily cap (per calendar day of its schedule) is reached (skipped), over a daily
/// limit (failed: quota), or a group the extension does not know (failed). It keeps the anti-ban gap between two posts
/// of the account, waits out a group's cooldown, and hands out nothing while the device is paused (by the web or by
/// the engine) or the workspace's failure rate says to stop.
/// </summary>
public sealed record ClaimJobCommand : ICommand<JobDto?>;

public sealed class ClaimJobCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts,
    IPostRepository posts, IMediaRepository media, IUserRepository users, IPlanRepository plans, ISetLinkRepository links,
    IScheduleRepository schedules, ICollectionRepository collections, IPostBumpRepository bumps, IDeviceEventRepository events,
    ScheduleTopUp topUp, INotificationDispatcher notifier, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ClaimJobCommand, JobDto?>
{
    /// <summary>The failure rate only stops the engine once this many posts finished in the last 24 hours.</summary>
    public const int MinFinishedForHalt = 10;

    public async Task<JobDto?> HandleAsync(ClaimJobCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // Schedules are kept a fortnight ahead; this is where they catch up with the clock (its own transaction, and it
        // never throws: a schedule that cannot be generated is logged and left for later).
        await topUp.EnsureAsync(current.WorkspaceId, ct);

        JobDto? job = null;
        Workspace? ws = null;
        var notes = new Notes();
        try
        {
            // One claim of a device at a time: the lock is held until the commit, so two overlapping claims (a retry, a
            // slow answer) cannot both hand out the same post or two posts at once. A post that still changes under a claim
            // (the web app edited it) fails the save with a conflict instead.
            await uow.ExecuteInTransactionAsync($"claim:{current.DeviceId:N}", async () =>
            {
                uow.DiscardChanges(); // what was loaded before the lock (the device at sign-in) may be out of date
                var device = await DeviceAccess.RequireAsync(devices, current, ct);
                if (device.Seen(null, now))
                    events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
                ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
                job = await NextAsync(device, ws, now, notes, ct);
                // A bump is a comment, not a post: it has no event of its own for the posts list.
                if (job is { Kind: "post" })
                    events.Add(DeviceEvents.Make(ws.Id, device.Id, DeviceEventType.Post, new { postId = job.PostId, status = PostStatus.Posting }, now));
                await uow.SaveChangesAsync(ct);
            }, ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Another request changed a post this claim was settling, or the lock was not granted in time. Nothing was
            // saved: there is no job now, and the device asks again in a moment.
            uow.DiscardChanges();
            return null;
        }

        // Telling people comes after the save, and never fails the claim.
        var notices = new List<Notice>();
        if (notes.QuotaFailed > 0) notices.Add(EngineNotices.QuotaReached(notes.QuotaFailed, notes.QuotaDetail!));
        if (notes.Slots.Count > 0)
        {
            try
            {
                notices.AddRange(await EngineNotices.FinishedRoundsAsync(posts, schedules, ws!.Id, notes.Slots, ct));
            }
            catch (Exception)
            {
                // A round summary is a courtesy.
            }
        }
        await EngineNotices.SendAsync(notifier, ws!.Id, notices, ct);
        return job;
    }

    /// <summary>What a claim settled on the way, for the messages sent afterwards.</summary>
    private sealed class Notes
    {
        public int QuotaFailed { get; private set; }
        public string? QuotaDetail { get; private set; }
        public HashSet<(Guid ScheduleId, string SlotKey)> Slots { get; } = [];

        public void Quota(string detail)
        {
            QuotaFailed++;
            QuotaDetail ??= detail;
        }

        /// <summary>The post is finished without a result from the device: its round may be over.</summary>
        public void Settled(Post p)
        {
            if (p.ScheduleId is { } id && p.SlotKey is { } slot) Slots.Add((id, slot));
        }
    }

    // A browser that is new has a daily limit lower than the platform's while warm-up is on.
    private static int WarmupLimit(TimeSpan age) => age.TotalDays < 3 ? 3 : age.TotalDays < 7 ? 6 : age.TotalDays < 14 ? 12 : int.MaxValue;

    private async Task<JobDto?> NextAsync(Device device, Workspace ws, DateTimeOffset now, Notes notes, CancellationToken ct)
    {
        // A post the device never reported on is not retried automatically: it may have gone out.
        var claimed = await posts.ListClaimedByAsync(device.Id, ct);
        foreach (var p in claimed.Where(p => p.ClaimExpired(now)))
        {
            p.Fail(FailureCode.Network, "ไม่ได้รับผลการโพสต์จากส่วนขยายภายใน 15 นาที", now);
            events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
            notes.Settled(p);
        }
        if (claimed.Any(p => !p.ClaimExpired(now))) return null; // one post at a time
        // A bump the browser took and never reported is not retried either (the comment may be there).
        var claimedBumps = await bumps.ListClaimedByAsync(device.Id, ct);
        foreach (var b in claimedBumps.Where(b => b.ClaimExpired(now))) b.Fail("ไม่ได้รับผลการดันโพสต์จากส่วนขยายภายใน 15 นาที", now);
        if (claimedBumps.Any(b => !b.ClaimExpired(now))) return null;

        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        DevicePause.Settle(device, events, now); // a pause that ran out ends here
        if (device.JobsPaused || device.IsAutoPaused(now) || !ws.ExtensionOnline || account is null || !account.CanPost) return null;
        var owner = await users.OwnerOfAsync(ws, ct);
        if (!owner.CanPost) return null; // suspended, banned or paused by the platform admin

        var advanced = ws.AntiBan.Advanced;
        var gap = TimeSpan.FromMinutes(Math.Max(ws.AntiBan.Min, advanced.MinGap));
        // The pause between two actions of one account counts bumps too: a comment right after a post is as fast as two posts.
        var lastPosted = await posts.LastPublishedAtAsync(account.Id, ct);
        var lastBump = await bumps.LastDoneAtAsync(account.Id, ct);
        var last = lastPosted is { } lp0 && lastBump is { } lb ? (lp0 > lb ? lp0 : lb) : lastPosted ?? lastBump;
        if (last is { } l && now - l < gap) return null;

        var due = await posts.ListDueAsync(account.Id, now, ct);
        if (due.Count == 0) return await NextBumpAsync(device, ws, account, owner, now, ct);

        // Too many of the last day's posts failed: the engine stops until they age out (or the numbers are changed).
        if (advanced.StopFailPct > 0)
        {
            var (finished, failed) = await posts.CountOutcomesSinceAsync(ws.Id, now.AddDays(-1), ct);
            if (finished >= MinFinishedForHalt && failed * 100 > advanced.StopFailPct * finished) return null;
        }

        var limit = ws.AntiBan.Limits.For(account.Platform);
        var warmedUp = ws.AntiBan.Warmup && WarmupLimit(now - device.CreatedAt) < limit;
        if (ws.AntiBan.Warmup) limit = Math.Min(limit, WarmupLimit(now - device.CreatedAt));
        var sentToday = await posts.CountPublishedSinceAsync(ws.Id, account.Platform, now.AddDays(-1), ct);
        var sentAll = advanced.DailyAll > 0 ? await posts.CountPublishedInWorkspaceSinceAsync(ws.Id, now.AddDays(-1), ct) : 0;
        // The plan's posts per 24 hours count every workspace of the owner.
        var planPosts = (await plans.ForAsync(owner, ct)).Posts;
        var ownWorkspaces = (await workspaces.ListByOwnerAsync(owner.Id, ct)).Select(w => w.Id).ToList();
        var sentByOwner = planPosts is null ? 0 : await posts.CountPublishedSinceAsync(ownWorkspaces, now.AddDays(-1), ct);
        // The anti-ban gap is pacing, not lateness: a post held back by it is not "late" for the offline policy
        // (otherwise two posts due together would leave the second one skipped under the skip policy).
        var openFrom = last is { } lp ? lp + gap : DateTimeOffset.MinValue;

        var linkCache = new Dictionary<Guid, SetLink?>();
        var perLinkDay = new Dictionary<(Guid Link, DateTimeOffset Day), int>();
        var offsets = new Dictionary<Guid, int>();
        var lastToLink = new Dictionary<Guid, DateTimeOffset?>();

        // A group's daily cap counts calendar days, in the calendar of the schedule that made the post (UTC without one).
        async Task<(DateTimeOffset Start, DateTimeOffset End)> DayOfAsync(Post p)
        {
            var offset = 0;
            if (p.ScheduleId is { } scheduleId && !offsets.TryGetValue(scheduleId, out offset))
                offsets[scheduleId] = offset = (await schedules.GetAsync(ws.Id, scheduleId, ct))?.UtcOffsetMinutes ?? 0;
            return Schedule.LocalDayBounds(p.ScheduledAt, offset);
        }

        void Settle(Post p)
        {
            events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
            notes.Settled(p);
        }

        foreach (var p in due)
        {
            SetLink? link = null;
            var holdUntil = DateTimeOffset.MinValue;
            if (p.LinkId is { } linkId)
            {
                if (!linkCache.TryGetValue(linkId, out link)) linkCache[linkId] = link = await links.GetAsync(ws.Id, linkId, ct);
                if (link is null || !link.Enabled)
                {
                    p.Skip(now, "กลุ่มถูกปิดหรือถูกลบแล้ว");
                    Settle(p);
                    continue;
                }
                if (link.DailyMax > 0)
                {
                    var (dayStart, dayEnd) = await DayOfAsync(p);
                    if (!perLinkDay.TryGetValue((linkId, dayStart), out var published))
                        perLinkDay[(linkId, dayStart)] = published = await posts.CountPublishedToLinkAsync(linkId, dayStart, dayEnd, ct);
                    if (published >= link.DailyMax)
                    {
                        p.Skip(now, "ครบเพดานต่อวันของกลุ่มนี้");
                        Settle(p);
                        continue;
                    }
                }
                if (advanced.Cooldown > 0)
                {
                    if (!lastToLink.TryGetValue(linkId, out var lastPost))
                        lastToLink[linkId] = lastPost = await posts.LastPublishedToLinkAtAsync(linkId, ct);
                    if (lastPost is { } lastAt) holdUntil = lastAt + TimeSpan.FromHours(advanced.Cooldown);
                }
            }

            // A group's cooldown is pacing too: lateness counts from when it ends.
            if (now - Latest(p.ScheduledAt, openFrom, holdUntil) > ws.Offline.MaxLateness)
            {
                p.SkipLate(now);
                Settle(p);
                continue;
            }
            if (holdUntil > now) continue; // the group had a post lately: it stays queued
            if (sentToday >= limit)
            {
                var detail = warmedUp
                    ? $"ครบโควตาช่วงอุ่นเครื่อง {limit} โพสต์ใน 24 ชั่วโมง"
                    : $"ครบโควตา {limit} โพสต์ใน 24 ชั่วโมงของแพลตฟอร์มนี้";
                p.Fail(FailureCode.Quota, detail, now);
                Settle(p);
                notes.Quota(detail);
                continue;
            }
            if (advanced.DailyAll > 0 && sentAll >= advanced.DailyAll)
            {
                var detail = $"ครบเพดานรวม {advanced.DailyAll} โพสต์ใน 24 ชั่วโมง (ทุกแพลตฟอร์ม)";
                p.Fail(FailureCode.Quota, detail, now);
                Settle(p);
                notes.Quota(detail);
                continue;
            }
            if (planPosts is { } pp && sentByOwner >= pp)
            {
                var detail = $"ครบโควตา {pp} โพสต์ต่อวันของแผน อัปเกรดแผนเพื่อโพสต์ได้มากขึ้น";
                p.Fail(FailureCode.Quota, detail, now);
                Settle(p);
                notes.Quota(detail);
                continue;
            }
            // Posts that came from a link of a link set carry the address; older ones look the group up by name.
            var url = p.TargetUrl ?? account.UrlFor(p.Target);
            if (url is null)
            {
                p.Fail(FailureCode.Network, $"ไม่พบลิงก์ของกลุ่ม \"{p.Target}\" ในส่วนขยาย", now);
                Settle(p);
                continue;
            }
            p.Claim(device.Id, now);
            var files = (await media.ListAsync(ws.Id, ct)).ToDictionary(f => f.Id);
            var items = p.MediaIds
                .Where(files.ContainsKey)
                .Select(id => new JobMediaDto(id, files[id].Name, files[id].ContentType))
                .ToList();
            // The page tags are the schedule's collection's (an empty text = tag nobody): the extension has no page of its own.
            var pageTags = "";
            if (p.ScheduleId is { } scheduleOfPost && await schedules.GetAsync(ws.Id, scheduleOfPost, ct) is { } sch)
                pageTags = (await collections.GetAsync(ws.Id, sch.CollectionId, ct))?.Settings.PageTags ?? "";
            var kind = FacebookGroupUrl.KindOf(url) == FacebookTargetKind.Page ? "page" : "group";
            return new JobDto(p.Id, p.Target, url, p.Content, items, AntiBanDto.From(ws.AntiBan), "post", kind, pageTags);
        }
        // Nothing to post right now (everything due was settled or is held back): a bump may go instead.
        return await NextBumpAsync(device, ws, account, owner, now, ct);
    }

    /// <summary>
    /// The next comment that bumps a post of this account, or null. Only for an owner whose plan includes bumping; one that
    /// is long overdue is dropped (a bump a day late is no bump), and so is one whose schedule is gone.
    /// </summary>
    private async Task<JobDto?> NextBumpAsync(Device device, Workspace ws, SocialAccount account, User owner, DateTimeOffset now, CancellationToken ct)
    {
        if (!owner.HasBump) return null;
        foreach (var b in await bumps.ListDueAsync(account.Id, now, ct))
        {
            if (b.TooLate(now))
            {
                b.Skip("เลยเวลาดันโพสต์แล้ว", now);
                continue;
            }
            if (b.ScheduleId is { } scheduleId && await schedules.GetAsync(ws.Id, scheduleId, ct) is null)
            {
                b.Skip("ตารางโพสต์ถูกลบแล้ว", now);
                continue;
            }
            b.Claim(device.Id, now);
            var files = (await media.ListAsync(ws.Id, ct)).ToDictionary(f => f.Id);
            var items = b.MediaIds
                .Where(files.ContainsKey)
                .Select(id => new JobMediaDto(id, files[id].Name, files[id].ContentType))
                .ToList();
            return new JobDto(b.Id, b.Target, b.Url, b.Text, items, AntiBanDto.From(ws.AntiBan), "bump");
        }
        return null;
    }

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b, DateTimeOffset c) => a > b ? (a > c ? a : c) : (b > c ? b : c);
}

/// <param name="NeedsLogin">Facebook was logged out or asked for a checkpoint: the account needs a new login.</param>
/// <param name="Blocked">Facebook showed a warning or a posting limit.</param>
/// <param name="AwaitingApproval">The group holds the post for admin approval.</param>
/// <param name="PostUrl">Where the post went up on Facebook, when the extension could read it: what a bump opens.</param>
public sealed record ReportJobResultCommand(
    Guid PostId, bool Ok, bool AwaitingApproval, bool NeedsLogin, bool Blocked, string? Error, string? PostUrl = null) : ICommand<PostDto>;

/// <summary>
/// Settles a post the device reported on, and what follows from it: the link's health (a group that kept failing is
/// switched off), the device's pause (Facebook blocked it, or too many posts failed in a row), and the messages to
/// Telegram/LINE. Failures caused by the account (a lost login, a Facebook block) say nothing about the group, so they
/// do not count against the link.
/// </summary>
public sealed class ReportJobResultCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts, IPostRepository posts,
    ISetLinkRepository links, IScheduleRepository schedules, IUserRepository users, IPostBumpRepository bumps, IDeviceEventRepository events,
    INotificationDispatcher notifier, IRandomSource random, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ReportJobResultCommand, PostDto>
{
    /// <summary>Hours a device rests after too many failed posts in a row.</summary>
    public const double StreakPauseMinHours = 2;
    public const double StreakPauseMaxHours = 4;

    public async Task<PostDto> HandleAsync(ReportJobResultCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        if (device.Seen(null, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        var post = await posts.GetAsync(device.WorkspaceId, c.PostId, ct);
        if (post is null || post.ClaimedByDeviceId != device.Id) throw new NotFoundException("งานโพสต์", c.PostId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var advanced = ws.AntiBan.Advanced;
        var link = post.LinkId is { } linkId ? await links.GetAsync(ws.Id, linkId, ct) : null;
        var notices = new List<Notice>();

        if (c.Ok)
        {
            post.CompletePosted(c.AwaitingApproval, now);
            // Where it went up on Facebook (a bump opens it); nothing to bump while the group still has to approve it.
            if (!c.AwaitingApproval)
            {
                post.RecordPostUrl(c.PostUrl);
                if (post.ScheduleId is { } bumpScheduleId && post.PostUrl is not null
                    && await schedules.GetAsync(ws.Id, bumpScheduleId, ct) is { BumpHours: > 0 } bumpSchedule
                    && (await users.OwnerOfAsync(ws, ct)).HasBump)
                    bumps.AddRange(BumpPlanner.Plan(post, bumpSchedule, now, random.NextDouble));
            }
            account?.MarkHealthy();
            if (link is not null)
            {
                var health = link.Health;
                link.RecordSuccess();
                if (c.AwaitingApproval) link.RecordPendingApproval();
                if (link.Health != health) events.Add(DeviceEvents.LinksChanged(ws.Id, device.Id, link.LinkSetId, link.Id, link.Health, now));
            }
            notices.Add(EngineNotices.Posted(post, link?.LinkSetId, c.AwaitingApproval));
        }
        else
        {
            var code = c.NeedsLogin ? FailureCode.Session : c.Blocked ? FailureCode.RateLimit : FailureCode.Network;
            post.Fail(code, c.Error, now);
            if (c.NeedsLogin) account?.MarkNeedsLogin();

            // A lost login or a Facebook block is about the account, not the group.
            Notice? switchedOff = null;
            if (link is not null && !c.NeedsLogin && !c.Blocked)
            {
                link.RecordFailure();
                if (advanced.AutoOffFails > 0 && link.FailStreak >= advanced.AutoOffFails && link.Enabled)
                {
                    link.AutoDisable(link.FailStreak);
                    events.Add(DeviceEvents.LinksChanged(ws.Id, device.Id, link.LinkSetId, link.Id, link.Health, now));
                    switchedOff = EngineNotices.LinkSwitchedOff(link, link.FailStreak);
                }
            }

            TimeSpan? blockPause = null;
            if (c.Blocked && ws.AntiBan.AutoPause)
            {
                blockPause = TimeSpan.FromHours(advanced.BlockMin + random.NextDouble() * (advanced.BlockMax - advanced.BlockMin));
                if (device.AutoPause(now + blockPause.Value, "Facebook ขัดขวางการโพสต์ ระบบพักเครื่องชั่วคราว", now))
                    events.Add(DevicePause.Changed(device, now));
            }
            if (c.NeedsLogin) notices.Add(EngineNotices.NeedsLogin(post, link?.LinkSetId, device));
            else if (c.Blocked) notices.Add(EngineNotices.Blocked(post, link?.LinkSetId, device, c.Error, blockPause));
            else notices.Add(EngineNotices.Failed(post, link?.LinkSetId, c.Error));
            if (switchedOff is not null) notices.Add(switchedOff);

            // This post and the ones before it all failed: rest the device for a while.
            if (advanced.FailStreak > 0 && account is not null)
            {
                var before = advanced.FailStreak > 1 ? await posts.ListRecentOutcomesAsync(account.Id, advanced.FailStreak - 1, ct) : [];
                if (before.Count == advanced.FailStreak - 1 && before.All(s => s == PostStatus.Failed))
                {
                    var rest = TimeSpan.FromHours(StreakPauseMinHours + random.NextDouble() * (StreakPauseMaxHours - StreakPauseMinHours));
                    if (device.AutoPause(now + rest, $"โพสต์ล้มเหลวติดกัน {advanced.FailStreak} ครั้ง ระบบพักเครื่องชั่วคราว", now))
                    {
                        events.Add(DevicePause.Changed(device, now));
                        notices.Add(EngineNotices.FailStreak(device, advanced.FailStreak, rest));
                    }
                }
            }
        }
        events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, post, now));
        await uow.SaveChangesAsync(ct);

        if (post.ScheduleId is { } scheduleId && post.SlotKey is { } slot)
        {
            try
            {
                notices.AddRange(await EngineNotices.FinishedRoundsAsync(posts, schedules, ws.Id, [(scheduleId, slot)], ct));
            }
            catch (Exception)
            {
                // A round summary is a courtesy.
            }
        }
        await EngineNotices.SendAsync(notifier, ws.Id, notices, ct);
        return PostDto.From(post);
    }
}

/// <summary>The extension's report on a bump it took: the comment was made, or why not.</summary>
public sealed record ReportBumpResultCommand(Guid BumpId, bool Ok, bool NeedsLogin, bool Blocked, string? Error) : ICommand<Unit>;

public sealed class ReportBumpResultCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts,
    IPostBumpRepository bumps, IDeviceEventRepository events, IRandomSource random, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ReportBumpResultCommand, Unit>
{
    public async Task<Unit> HandleAsync(ReportBumpResultCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        if (device.Seen(null, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        var bump = await bumps.GetAsync(device.WorkspaceId, c.BumpId, ct);
        if (bump is null || bump.ClaimedByDeviceId != device.Id) throw new NotFoundException("งานดันโพสต์", c.BumpId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        if (c.Ok)
        {
            bump.Complete(now);
            account?.MarkHealthy();
        }
        else
        {
            bump.Fail(c.Error, now);
            if (c.NeedsLogin) account?.MarkNeedsLogin();
            // A Facebook warning on a comment is a warning all the same: rest the browser like after a blocked post.
            if (c.Blocked)
            {
                var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
                if (ws.AntiBan.AutoPause)
                {
                    var advanced = ws.AntiBan.Advanced;
                    var rest = TimeSpan.FromHours(advanced.BlockMin + random.NextDouble() * (advanced.BlockMax - advanced.BlockMin));
                    if (device.AutoPause(now + rest, "Facebook ขัดขวางการดันโพสต์ ระบบพักเครื่องชั่วคราว", now))
                        events.Add(DevicePause.Changed(device, now));
                }
            }
        }
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>A library file attached to a post, for the device of the same workspace.</summary>
public sealed record GetDeviceMediaQuery(Guid MediaId) : IQuery<MediaContent>;

public sealed class GetDeviceMediaQueryHandler(ICurrentDevice current, IMediaRepository media)
    : IQueryHandler<GetDeviceMediaQuery, MediaContent>
{
    public async Task<MediaContent> HandleAsync(GetDeviceMediaQuery q, CancellationToken ct = default)
    {
        var file = await media.GetAsync(current.WorkspaceId, q.MediaId, ct) ?? throw new NotFoundException("ไฟล์", q.MediaId);
        return new MediaContent(file.Name, file.ContentType, file.Data, file.ExternalUrl);
    }
}

internal static class DeviceAccess
{
    public static async Task<Device> RequireAsync(IDeviceRepository devices, ICurrentDevice current, CancellationToken ct) =>
        await devices.GetByIdAsync(current.DeviceId, ct) ?? throw new AuthenticationException("อุปกรณ์นี้ถูกยกเลิกการผูกแล้ว");
}

/// <summary>The engine's own pause of a device (a Facebook block, posts that kept failing).</summary>
internal static class DevicePause
{
    /// <summary>The device's `device.updated` event: its name, the web's pause and the engine's own (absent when there is none).</summary>
    public static DeviceEvent Changed(Device device, DateTimeOffset now) =>
        DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Updated,
            new { name = device.Name, jobsPaused = device.JobsPaused, autoPausedUntil = device.AutoPausedUntil, autoPauseReason = device.AutoPauseReason }, now);

    /// <summary>The pause ran out: clears it and says so, once.</summary>
    public static void Settle(Device device, IDeviceEventRepository events, DateTimeOffset now)
    {
        if (device.EndAutoPause(now)) events.Add(Changed(device, now));
    }
}
