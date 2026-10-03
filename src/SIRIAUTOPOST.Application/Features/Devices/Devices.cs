using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
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
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, IPostRepository posts,
    IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RevokeDeviceCommand, Unit>
{
    public async Task<Unit> HandleAsync(RevokeDeviceCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        await DeviceRevocation.RevokeAsync(device, "ยกเลิกการผูกอุปกรณ์ระหว่างโพสต์", accounts, posts, devices, events, clock.GetUtcNow(), ct);
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
        Device device, string whilePostingDetail, IAccountRepository accounts, IPostRepository posts, IDeviceRepository devices,
        IDeviceEventRepository events, DateTimeOffset now, CancellationToken ct)
    {
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        account?.Disconnect();
        foreach (var p in await posts.ListClaimedByAsync(device.Id, ct))
        {
            p.Fail(FailureCode.Network, whilePostingDetail, now);
            events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
        }
        if (account is not null)
            foreach (var p in await posts.ListOpenByAccountAsync(account.Id, ct))
            {
                p.Fail(FailureCode.Session, QueuedDetail, now);
                events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
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
            device.Rename(c.Name);
            account?.FollowDevice(device);
        }
        if (c.JobsPaused is { } paused) device.SetJobsPaused(paused);
        var now = clock.GetUtcNow();
        events.Add(DeviceEvents.Make(ws.Id, device.Id, DeviceEventType.Updated, new { name = device.Name, jobsPaused = device.JobsPaused }, now));
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
        var device = Device.Pair(ws.Id, c.Name, c.Browser ?? "", c.Version ?? "", secrets.Hash(key), now);
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
        await uow.SaveChangesAsync(ct);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        // "Paused" is also what a suspended, banned or paused customer's device is told: it gets no jobs.
        var owner = await users.OwnerOfAsync(ws, ct);
        return new DeviceStatusDto(device.Id, device.Name, ws.Id, ws.Name, account?.Id, account?.GroupLinks.Count ?? 0,
            ws.ExtensionOnline, AntiBanDto.From(ws.AntiBan), device.JobsPaused || !owner.CanPost);
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
/// Hands the device the next post to publish, or nothing. On the way it settles posts it cannot hand out:
/// too late for the offline policy (skipped), over the platform's daily limit (failed: quota), or a group the
/// extension does not know (failed). It keeps the anti-ban minimum gap between two posts of the account.
/// </summary>
public sealed record ClaimJobCommand : ICommand<JobDto?>;

public sealed class ClaimJobCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts,
    IPostRepository posts, IMediaRepository media, IUserRepository users, IPlanRepository plans, IDeviceEventRepository events,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ClaimJobCommand, JobDto?>
{
    public async Task<JobDto?> HandleAsync(ClaimJobCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        if (device.Seen(null, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var job = await NextAsync(device, ws, now, ct);
        if (job is not null)
            events.Add(DeviceEvents.Make(ws.Id, device.Id, DeviceEventType.Post, new { postId = job.PostId, status = PostStatus.Posting }, now));
        await uow.SaveChangesAsync(ct);
        return job;
    }

    private async Task<JobDto?> NextAsync(Device device, Workspace ws, DateTimeOffset now, CancellationToken ct)
    {
        // A post the device never reported on is not retried automatically: it may have gone out.
        var claimed = await posts.ListClaimedByAsync(device.Id, ct);
        foreach (var p in claimed.Where(p => p.ClaimExpired(now)))
        {
            p.Fail(FailureCode.Network, "ไม่ได้รับผลการโพสต์จากส่วนขยายภายใน 15 นาที", now);
            events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
        }
        if (claimed.Any(p => !p.ClaimExpired(now))) return null; // one post at a time

        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        if (device.JobsPaused || !ws.ExtensionOnline || account is null || !account.CanPost) return null;
        var owner = await users.OwnerOfAsync(ws, ct);
        if (!owner.CanPost) return null; // suspended, banned or paused by the platform admin

        var last = await posts.LastPublishedAtAsync(account.Id, ct);
        if (last is { } l && now - l < TimeSpan.FromMinutes(ws.AntiBan.Min)) return null;

        var limit = ws.AntiBan.Limits.For(account.Platform);
        var sentToday = await posts.CountPublishedSinceAsync(ws.Id, account.Platform, now.AddDays(-1), ct);
        // The plan's posts per 24 hours count every workspace of the owner.
        var planPosts = (await plans.ForAsync(owner, ct)).Posts;
        var ownWorkspaces = (await workspaces.ListByOwnerAsync(owner.Id, ct)).Select(w => w.Id).ToList();
        var sentByOwner = planPosts is null ? 0 : await posts.CountPublishedSinceAsync(ownWorkspaces, now.AddDays(-1), ct);
        // The anti-ban gap is pacing, not lateness: a post held back by it is not "late" for the offline policy
        // (otherwise two posts due together would leave the second one skipped under the skip policy).
        var openFrom = last is { } lp ? lp + TimeSpan.FromMinutes(ws.AntiBan.Min) : DateTimeOffset.MinValue;
        foreach (var p in await posts.ListDueAsync(account.Id, now, ct))
        {
            if (now - (p.ScheduledAt > openFrom ? p.ScheduledAt : openFrom) > ws.Offline.MaxLateness)
            {
                p.SkipLate(now);
                events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
                continue;
            }
            if (sentToday >= limit)
            {
                p.Fail(FailureCode.Quota, $"ครบโควตา {limit} โพสต์ใน 24 ชั่วโมงของแพลตฟอร์มนี้", now);
                events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
                continue;
            }
            if (planPosts is { } pp && sentByOwner >= pp)
            {
                p.Fail(FailureCode.Quota, $"ครบโควตา {pp} โพสต์ต่อวันของแผน อัปเกรดแผนเพื่อโพสต์ได้มากขึ้น", now);
                events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
                continue;
            }
            var url = account.UrlFor(p.Target);
            if (url is null)
            {
                p.Fail(FailureCode.Network, $"ไม่พบลิงก์ของกลุ่ม \"{p.Target}\" ในส่วนขยาย", now);
                events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
                continue;
            }
            p.Claim(device.Id, now);
            var files = (await media.ListAsync(ws.Id, ct)).ToDictionary(f => f.Id);
            var items = p.MediaIds
                .Where(files.ContainsKey)
                .Select(id => new JobMediaDto(id, files[id].Name, files[id].ContentType))
                .ToList();
            return new JobDto(p.Id, p.Target, url, p.Content, items, AntiBanDto.From(ws.AntiBan));
        }
        return null;
    }
}

/// <param name="NeedsLogin">Facebook was logged out or asked for a checkpoint: the account needs a new login.</param>
/// <param name="Blocked">Facebook showed a warning or a posting limit.</param>
/// <param name="AwaitingApproval">The group holds the post for admin approval.</param>
public sealed record ReportJobResultCommand(
    Guid PostId, bool Ok, bool AwaitingApproval, bool NeedsLogin, bool Blocked, string? Error) : ICommand<PostDto>;

public sealed class ReportJobResultCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IAccountRepository accounts, IPostRepository posts,
    IDeviceEventRepository events, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ReportJobResultCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(ReportJobResultCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        if (device.Seen(null, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        var post = await posts.GetAsync(device.WorkspaceId, c.PostId, ct);
        if (post is null || post.ClaimedByDeviceId != device.Id) throw new NotFoundException("งานโพสต์", c.PostId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        if (c.Ok)
        {
            post.CompletePosted(c.AwaitingApproval, now);
            account?.MarkHealthy();
        }
        else
        {
            var code = c.NeedsLogin ? FailureCode.Session : c.Blocked ? FailureCode.RateLimit : FailureCode.Network;
            post.Fail(code, c.Error, now);
            if (c.NeedsLogin) account?.MarkNeedsLogin();
        }
        events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, post, now));
        await uow.SaveChangesAsync(ct);
        return PostDto.From(post);
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
        return new MediaContent(file.Name, file.ContentType, file.Data);
    }
}

internal static class DeviceAccess
{
    public static async Task<Device> RequireAsync(IDeviceRepository devices, ICurrentDevice current, CancellationToken ct) =>
        await devices.GetByIdAsync(current.DeviceId, ct) ?? throw new AuthenticationException("อุปกรณ์นี้ถูกยกเลิกการผูกแล้ว");
}
