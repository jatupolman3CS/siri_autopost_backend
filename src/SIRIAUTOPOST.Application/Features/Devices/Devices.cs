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
        var ws = await workspaces.RequireOwnedAsync(q.WorkspaceId, current, ct);
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
    IWorkspaceRepository workspaces, IUserRepository users, IDeviceRepository devices, IDevicePairingRepository pairings,
    IDeviceSecrets secrets, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreatePairingCodeCommand, PairingCodeDto>
{
    public async Task<PairingCodeDto> HandleAsync(CreatePairingCodeCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireOwnedAsync(c.WorkspaceId, current, ct);
        var max = await DeviceLimit.EnsureRoomAsync(ws, users, devices, ct);
        var pairing = DevicePairing.Create(ws.Id, secrets.NewPairingCode(), clock.GetUtcNow());
        pairings.Add(pairing);
        await uow.SaveChangesAsync(ct);
        return new PairingCodeDto(pairing.Code, pairing.ExpiresAt, max);
    }
}

/// <summary>Unbinds a device. Its account keeps its history but needs a new pairing to post again.</summary>
public sealed record RevokeDeviceCommand(Guid WorkspaceId, Guid DeviceId) : ICommand<Unit>;

public sealed class RevokeDeviceCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, IPostRepository posts,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RevokeDeviceCommand, Unit>
{
    public async Task<Unit> HandleAsync(RevokeDeviceCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireOwnedAsync(c.WorkspaceId, current, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var now = clock.GetUtcNow();
        (await accounts.GetByDeviceAsync(device.Id, ct))?.Disconnect();
        foreach (var p in await posts.ListClaimedByAsync(device.Id, ct))
            p.Fail(FailureCode.Network, "ยกเลิกการผูกอุปกรณ์ระหว่างโพสต์", now);
        devices.Remove(device);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal static class DeviceLimit
{
    /// <summary>Returns the plan's device limit, or throws when the workspace already has that many.</summary>
    public static async Task<int?> EnsureRoomAsync(Workspace ws, IUserRepository users, IDeviceRepository devices, CancellationToken ct)
    {
        var owner = await users.GetByIdAsync(ws.OwnerId, ct) ?? throw new NotFoundException("ผู้ใช้", ws.OwnerId);
        var max = PlanRules.MaxDevices(owner.Plan);
        if (max is { } m && await devices.CountAsync(ws.Id, ct) >= m)
            throw new DomainException($"แผนปัจจุบันผูกอุปกรณ์ได้สูงสุด {m} เครื่อง ยกเลิกการผูกเครื่องเดิมหรืออัปเกรดแผนก่อน");
        return max;
    }
}

// ---------- device side (extension, X-Device-Key) ----------

/// <summary>The extension trades a pairing code for its device key, and gets a Facebook account in the workspace.</summary>
public sealed record PairDeviceCommand(string Code, string Name, string? Browser, string? Version) : ICommand<PairResultDto>;

public sealed class PairDeviceCommandHandler(
    IDevicePairingRepository pairings, IWorkspaceRepository workspaces, IUserRepository users, IDeviceRepository devices,
    IAccountRepository accounts, IDeviceSecrets secrets, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<PairDeviceCommand, PairResultDto>
{
    public async Task<PairResultDto> HandleAsync(PairDeviceCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var code = NormalizeCode(c.Code);
        var pairing = await pairings.GetByCodeAsync(code, ct) ?? throw new DomainException("ไม่พบรหัสจับคู่นี้ ตรวจสอบรหัสอีกครั้ง");
        pairing.Use(now);
        var ws = await workspaces.GetByIdAsync(pairing.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", pairing.WorkspaceId);
        await DeviceLimit.EnsureRoomAsync(ws, users, devices, ct);

        var key = secrets.NewDeviceKey();
        var device = Device.Pair(ws.Id, c.Name, c.Browser ?? "", c.Version ?? "", secrets.Hash(key), now);
        devices.Add(device);
        var account = SocialAccount.Connect(ws.Id, device, await accounts.CountAsync(ws.Id, ct));
        accounts.Add(account);
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
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeviceHeartbeatCommand, DeviceStatusDto>
{
    public async Task<DeviceStatusDto> HandleAsync(DeviceHeartbeatCommand c, CancellationToken ct = default)
    {
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        device.Seen(c.Version, clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        return new DeviceStatusDto(device.Id, device.Name, ws.Id, ws.Name, account?.Id, account?.GroupLinks.Count ?? 0,
            ws.ExtensionOnline, AntiBanDto.From(ws.AntiBan));
    }
}

/// <summary>The groups set up in the extension become the groups of the device's Facebook account.</summary>
public sealed record SyncDeviceGroupsCommand(IReadOnlyList<GroupLinkDto> Groups) : ICommand<int>;

public sealed class SyncDeviceGroupsCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IAccountRepository accounts, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SyncDeviceGroupsCommand, int>
{
    public async Task<int> HandleAsync(SyncDeviceGroupsCommand c, CancellationToken ct = default)
    {
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        device.Seen(null, clock.GetUtcNow());
        var account = await accounts.GetByDeviceAsync(device.Id, ct) ?? throw new NotFoundException("บัญชีของอุปกรณ์", device.Id);
        account.SyncGroups(c.Groups.Select(g => new GroupLink { Name = g.Name, Url = g.Url }));
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
    IPostRepository posts, IMediaRepository media, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ClaimJobCommand, JobDto?>
{
    public async Task<JobDto?> HandleAsync(ClaimJobCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        device.Seen(null, now);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var job = await NextAsync(device, ws, now, ct);
        await uow.SaveChangesAsync(ct);
        return job;
    }

    private async Task<JobDto?> NextAsync(Device device, Workspace ws, DateTimeOffset now, CancellationToken ct)
    {
        // A post the device never reported on is not retried automatically: it may have gone out.
        var claimed = await posts.ListClaimedByAsync(device.Id, ct);
        foreach (var p in claimed.Where(p => p.ClaimExpired(now)))
            p.Fail(FailureCode.Network, "ไม่ได้รับผลการโพสต์จากส่วนขยายภายใน 15 นาที", now);
        if (claimed.Any(p => !p.ClaimExpired(now))) return null; // one post at a time

        var account = await accounts.GetByDeviceAsync(device.Id, ct);
        if (!ws.ExtensionOnline || account is null || !account.CanPost) return null;

        var last = await posts.LastPublishedAtAsync(account.Id, ct);
        if (last is { } l && now - l < TimeSpan.FromMinutes(ws.AntiBan.Min)) return null;

        var limit = ws.AntiBan.Limits.For(account.Platform);
        var sentToday = await posts.CountPublishedSinceAsync(ws.Id, account.Platform, now.AddDays(-1), ct);
        foreach (var p in await posts.ListDueAsync(account.Id, now, ct))
        {
            if (now - p.ScheduledAt > ws.Offline.MaxLateness)
            {
                p.SkipLate(now);
                continue;
            }
            if (sentToday >= limit)
            {
                p.Fail(FailureCode.Quota, $"ครบโควตา {limit} โพสต์ใน 24 ชั่วโมงของแพลตฟอร์มนี้", now);
                continue;
            }
            var url = account.UrlFor(p.Target);
            if (url is null)
            {
                p.Fail(FailureCode.Network, $"ไม่พบลิงก์ของกลุ่ม \"{p.Target}\" ในส่วนขยาย", now);
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
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ReportJobResultCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(ReportJobResultCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        device.Seen(null, now);
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
