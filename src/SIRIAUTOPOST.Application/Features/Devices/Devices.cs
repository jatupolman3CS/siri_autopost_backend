using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Extension;
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
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, ILineChatRepository lineChats, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetDevicesQuery, IReadOnlyList<DeviceDto>>
{
    public async Task<IReadOnlyList<DeviceDto>> HandleAsync(GetDevicesQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var accList = await accounts.ListAsync(ws.Id, ct);
        var fbByDevice = accList
            .Where(a => a.DeviceId is not null && a.Platform == Platform.Fb)
            .ToDictionary(a => a.DeviceId!.Value, a => a.Id);
        // A browser can read several OAs now: the summary is of the one it reports (else the first).
        var linesByDevice = accList
            .Where(a => a.DeviceId is not null && a.Platform == Platform.Line)
            .ToLookup(a => a.DeviceId!.Value);
        var now = clock.GetUtcNow();
        var deviceList = await devices.ListAsync(ws.Id, ct);
        var dtos = new List<DeviceDto>();
        foreach (var d in deviceList)
        {
            var fbId = fbByDevice.TryGetValue(d.Id, out var fId) ? (Guid?)fId : null;
            var lines = linesByDevice[d.Id].ToList();
            var lineAcc = lines.FirstOrDefault(a => string.Equals(a.Handle, d.LineBotId, StringComparison.OrdinalIgnoreCase))
                ?? lines.FirstOrDefault();
            dtos.Add(DeviceDto.From(d, fbId, now, await DeviceLineSummary.OfAsync(d, lineAcc, lineChats, now, ct)));
        }
        return dtos;
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
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, ILineChatRepository lineChats, IPostRepository posts, IPostBumpRepository bumps,
    IDeviceEventRepository events, INotificationDispatcher notifier, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RevokeDeviceCommand, Unit>
{
    public async Task<Unit> HandleAsync(RevokeDeviceCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var cancelled = await DeviceRevocation.RevokeAsync(device, "ยกเลิกการผูกอุปกรณ์ระหว่างโพสต์", accounts, posts, bumps, devices, events, clock.GetUtcNow(), ct, lineChats);
        await uow.SaveChangesAsync(ct);
        await events.PruneAsync(device.Id, DeviceRevocation.KeepEvents, ct);
        await EngineNotices.SendAsync(notifier, ws.Id, [EngineNotices.DeviceUnbound(device, cancelled)], ct);
        return Unit.Value;
    }
}

/// <summary>What unbinding a device does, shared by the owner's team and the platform admin.</summary>
internal static class DeviceRevocation
{
    /// <summary>Events kept for a device that is gone: nothing prunes them later.</summary>
    public const int KeepEvents = 50;

    public const string QueuedDetail = "อุปกรณ์ถูกยกเลิกการผูกแล้ว ต้องจับคู่เครื่องใหม่ก่อนจึงจะโพสต์ได้";

    /// <summary>Disconnects the account, fails its unfinished posts and removes the device. The caller saves. Returns how many posts it cancelled.</summary>
    public static async Task<int> RevokeAsync(
        Device device, string whilePostingDetail, IAccountRepository accounts, IPostRepository posts, IPostBumpRepository bumps,
        IDeviceRepository devices, IDeviceEventRepository events, DateTimeOffset now, CancellationToken ct, ILineChatRepository lineChats)
    {
        var deviceAccounts = await accounts.ListByDeviceAsync(device.Id, ct);
        foreach (var acc in deviceAccounts)
        {
            acc.Disconnect();
            if (acc.Platform == Platform.Line) await lineChats.DeleteByAccountAsync(acc.Id, ct); // customers' names go with the browser
        }
        var cancelled = 0;
        foreach (var p in await posts.ListClaimedByAsync(device.Id, ct))
        {
            p.Fail(FailureCode.Network, whilePostingDetail, now);
            events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
            cancelled++;
        }
        foreach (var acc in deviceAccounts)
        {
            foreach (var p in await posts.ListOpenByAccountAsync(acc.Id, ct))
            {
                p.Fail(FailureCode.Session, QueuedDetail, now);
                events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
                cancelled++;
            }
            if (acc.Platform == Platform.Fb)
            {
                // Comments that were to bump its posts have no browser to make them any more.
                foreach (var b in await bumps.ListOpenByAccountAsync(acc.Id, ct)) b.Skip(QueuedDetail, now);
            }
        }
        devices.Remove(device);
        events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Revoked, new { name = device.Name }, now));
        return cancelled;
    }
}

/// <summary>
/// The web app's controls of a paired browser: its name (and its Facebook account's) and whether it takes
/// posts scheduled on the web. null leaves a value as it is.
/// </summary>
public sealed record UpdateDeviceCommand(Guid WorkspaceId, Guid DeviceId, string? Name, bool? JobsPaused) : ICommand<DeviceDto>;

public sealed class UpdateDeviceCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, ILineChatRepository lineChats, IPostRepository posts, IDeviceEventRepository events,
    INotificationDispatcher notifier, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateDeviceCommand, DeviceDto>
{
    public async Task<DeviceDto> HandleAsync(UpdateDeviceCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var fbAccount = await accounts.GetByDeviceAsync(device.Id, Platform.Fb, ct);
        if (c.Name is not null)
        {
            // Extensions of one workspace must have different names: they are told apart by name when posting.
            var wanted = c.Name.Trim();
            if (wanted.Length > 0 && (await devices.ListNamesAsync(ws.Id, device.Id, ct)).Any(n => Device.SameName(n, wanted)))
                throw new DomainException($"มีส่วนขยายชื่อ \"{wanted}\" อยู่ในบัญชีนี้แล้ว ตั้งชื่อที่ไม่ซ้ำกัน");
            device.Rename(c.Name);
            fbAccount?.FollowDevice(device);
        }
        var now = clock.GetUtcNow();
        var pauseChanged = false;
        if (c.JobsPaused is { } paused)
        {
            pauseChanged = paused != device.JobsPaused;
            device.SetJobsPaused(paused);
            if (!paused) device.EndAutoPause(now, force: true); // "resume" also lifts the engine's own pause
        }
        events.Add(DevicePause.Changed(device, now));
        await uow.SaveChangesAsync(ct);
        if (pauseChanged)
        {
            var waiting = fbAccount is null ? 0 : (await posts.ListOpenByAccountAsync(fbAccount.Id, ct)).Count;
            await EngineNotices.SendAsync(notifier, ws.Id, [EngineNotices.DeviceJobsPaused(device, device.JobsPaused, waiting)], ct);
        }
        var lineAcc = await accounts.GetByDeviceAsync(device.Id, Platform.Line, ct);
        return DeviceDto.From(device, fbAccount?.Id, now, await DeviceLineSummary.OfAsync(device, lineAcc, lineChats, now, ct));
    }
}

/// <summary>
/// "Change LINE OA": lets go of the browser's LINE Official Account so another one can be connected on it. The Line account
/// stays (its link sets wait, and it comes back to whichever browser reports that OA next) but loses its browser; its customer
/// chats are deleted like on unbinding, its unfinished chat posts fail, and the extension is told to forget the OA
/// (<c>resetLine</c>) so its next heartbeat no longer reports it. Facebook is not touched.
/// </summary>
public sealed record DisconnectDeviceLineCommand(Guid WorkspaceId, Guid DeviceId) : ICommand<DeviceDto>;

public sealed class DisconnectDeviceLineCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts, ILineChatRepository lineChats,
    IPostRepository posts, IExtensionRepository ext, IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DisconnectDeviceLineCommand, DeviceDto>
{
    public async Task<DeviceDto> HandleAsync(DisconnectDeviceLineCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var now = clock.GetUtcNow();
        var lineAcc = await accounts.GetByDeviceAsync(device.Id, Platform.Line, ct);
        if (lineAcc is not null)
        {
            foreach (var p in await posts.ListClaimedByAsync(device.Id, ct))
            {
                if (p.AccountId != lineAcc.Id) continue;
                p.Fail(FailureCode.Network, "ยกเลิกการเชื่อม LINE OA ระหว่างส่งข้อความ", now);
                events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
            }
            foreach (var p in await posts.ListOpenByAccountAsync(lineAcc.Id, ct))
            {
                p.Fail(FailureCode.Session, "ยกเลิกการเชื่อม LINE OA ของเครื่องนี้แล้ว ต้องเชื่อม LINE OA ใหม่ก่อนจึงจะส่งต่อได้", now);
                events.Add(DeviceEvents.PostChanged(device.WorkspaceId, device.Id, p, now));
            }
            lineAcc.Disconnect();
            await lineChats.DeleteByAccountAsync(lineAcc.Id, ct); // customers' names go with the browser
        }
        device.ClearLineReady();
        var cmd = DeviceCommand.Create(ws.Id, device.Id, "resetLine", "{}", now);
        ext.Add(cmd);
        events.Add(CommandEvents.Of(cmd, now));
        events.Add(DevicePause.Changed(device, now));
        await uow.SaveChangesAsync(ct);
        var fbAccount = await accounts.GetByDeviceAsync(device.Id, Platform.Fb, ct);
        return DeviceDto.From(device, fbAccount?.Id, now, null);
    }
}

/// <summary>The one place that turns a device's Line account into the summary the web shows (list, update).</summary>
internal static class DeviceLineSummary
{
    public static async Task<DeviceLineSummaryDto?> OfAsync(Device d, SocialAccount? lineAcc, ILineChatRepository lineChats, DateTimeOffset now, CancellationToken ct)
    {
        if (lineAcc is null) return null;
        var chatCount = await lineChats.CountByAccountAsync(lineAcc.Id, ct);
        var maxSynced = await lineChats.MaxSyncedAtAsync(lineAcc.Id, ct);
        return new DeviceLineSummaryDto(
            lineAcc.Id, lineAcc.Name, lineAcc.Handle, d.LineBotId ?? lineAcc.Handle, d.IsLineReady(now), d.LineReadyAt,
            lineAcc.RestUntil, maxSynced, chatCount, lineAcc.Health == AccountHealth.Relogin);
    }
}

internal static class DeviceLimit
{
    /// <summary>
    /// Returns the owner's device limit, or throws when the workspace already has that many devices, or the owner
    /// already has as many connected accounts (all workspaces) as the plan allows: each device brings one.
    /// <paramref name="replacing"/> is 1 when the new device takes the place of one the workspace already has (a browser
    /// that pairs again): that one is not counted.
    /// </summary>
    public static async Task<int?> EnsureRoomAsync(
        Workspace ws, IUserRepository users, IPlanRepository plans, IDeviceRepository devices, IAccountRepository accounts,
        IWorkspaceRepository workspaces, CancellationToken ct, int replacing = 0)
    {
        var owner = await users.OwnerOfAsync(ws, ct);
        var limits = await plans.ForAsync(owner, ct);
        if (limits.Devices is { } m && await devices.CountAsync(ws.Id, ct) - replacing >= m)
            throw new DomainException($"แผนปัจจุบันผูกอุปกรณ์ได้สูงสุด {m} เครื่อง ยกเลิกการผูกเครื่องเดิมหรืออัปเกรดแผนก่อน");
        if (limits.Accounts is { } a)
        {
            var own = (await workspaces.ListByOwnerAsync(owner.Id, ct)).Select(w => w.Id);
            if (await accounts.CountConnectedAsync(own, ct) - replacing >= a)
                throw new DomainException($"แผนปัจจุบันเชื่อมบัญชีได้สูงสุด {a} บัญชี อัปเกรดแผนก่อน");
        }
        return limits.Devices;
    }
}

// ---------- device side (extension, X-Device-Key) ----------

/// <summary>
/// The extension trades a pairing code for its device key, and gets a Facebook account in the workspace.
/// A browser that pairs AGAIN says who it was and keeps its account (with the link sets pinned to it, its groups and its
/// history) instead of becoming a second one: <paramref name="PreviousDeviceKey"/> is the key it still holds (the pairing
/// replaces that device), or <paramref name="PreviousAccountId"/> / <paramref name="PreviousName"/> is what it remembers
/// after the web unbound it. Without them a pairing is a new browser, as before.
/// </summary>
public sealed record PairDeviceCommand(
    string Code, string Name, string? Browser, string? Version,
    string? PreviousDeviceKey = null, Guid? PreviousAccountId = null, string? PreviousName = null) : ICommand<PairResultDto>;

public sealed class PairDeviceCommandHandler(
    IDevicePairingRepository pairings, IWorkspaceRepository workspaces, IUserRepository users, IPlanRepository plans,
    IDeviceRepository devices, IAccountRepository accounts, IPostRepository posts, IPostBumpRepository bumps, ILinkSetRepository linkSets,
    IDeviceEventRepository events, IDeviceSecrets secrets, INotificationDispatcher notifier, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<PairDeviceCommand, PairResultDto>
{
    public async Task<PairResultDto> HandleAsync(PairDeviceCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var code = NormalizeCode(c.Code);
        var pairing = await pairings.GetByCodeAsync(code, ct) ?? throw new DomainException("ไม่พบรหัสจับคู่นี้ ตรวจสอบรหัสอีกครั้ง");
        pairing.Use(now);
        var ws = await workspaces.GetByIdAsync(pairing.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", pairing.WorkspaceId);
        var (replaced, returning) = await ReturningAsync(ws, c, ct);
        // The device this pairing replaces is not an extra one: a browser that pairs again is never refused for the plan's device limit.
        await DeviceLimit.EnsureRoomAsync(ws, users, plans, devices, accounts, workspaces, ct, replacing: replaced is null ? 0 : 1);

        var key = secrets.NewDeviceKey();
        // A browser that comes back keeps the name it had in the web app (renamed there, say "ส่วนขยาย 1"), not the dated
        // default of this pairing. A second browser that pairs with the same name ("Chrome 6/10/2569") gets "(2)": names
        // are unique in a workspace.
        var wanted = returning is null ? c.Name : FirstFilled(replaced?.Name, c.PreviousName) ?? c.Name;
        var name = Device.UniqueName(wanted, await devices.ListNamesAsync(ws.Id, replaced?.Id, ct));
        var device = Device.Pair(ws.Id, name, c.Browser ?? "", c.Version ?? "", secrets.Hash(key), now);
        SocialAccount? account = null;
        // One transaction, two saves: the old device has to be gone before the new one takes its name (a unique index), and
        // the account has to point at the new device only after that device exists.
        await uow.ExecuteInTransactionAsync($"pair:{ws.Id:N}", async () =>
        {
            if (replaced is not null)
            {
                await ReplaceAsync(replaced, returning, now, ct);
                await uow.SaveChangesAsync(ct);
            }
            devices.Add(device);
            if (returning is not null)
            {
                returning.Reconnect(device);
                account = returning;
            }
            else
            {
                // A second extension is about to join: until now "no choice" meant the only one, so the sets that name
                // nobody are given to it first (otherwise they would belong to no machine the moment the new one pairs).
                if (LinkOwnership.OnlyConnected(await accounts.ListAsync(ws.Id, ct)) is { } only)
                    LinkOwnership.PinUnassigned(await linkSets.ListAsync(ws.Id, ct), only.Id);
                account = SocialAccount.Connect(ws.Id, device, await accounts.CountAsync(ws.Id, ct));
                accounts.Add(account);
            }
            events.Add(DeviceEvents.Make(ws.Id, device.Id, DeviceEventType.Paired, new { name = device.Name, accountId = account.Id }, now));
            await uow.SaveChangesAsync(ct);
        }, ct);
        if (replaced is not null) await events.PruneAsync(replaced.Id, DeviceRevocation.KeepEvents, ct);
        await EngineNotices.SendAsync(notifier, ws.Id, [EngineNotices.DevicePaired(device)], ct);
        return new PairResultDto(key, device.Id, device.Name, ws.Id, ws.Name, account!.Id);
    }

    /// <summary>
    /// Who this browser was: the device its old key belongs to in this workspace (it is replaced, and its account is
    /// the one to keep), else the account it remembers when that account has no browser any more. A key of another
    /// workspace, an account that another live browser holds, or nothing at all means a new browser.
    /// </summary>
    private async Task<(Device? Replaced, SocialAccount? Account)> ReturningAsync(Workspace ws, PairDeviceCommand c, CancellationToken ct)
    {
        var oldKey = (c.PreviousDeviceKey ?? "").Trim();
        if (oldKey.Length > 0 && await devices.GetByKeyHashAsync(secrets.Hash(oldKey), ct) is { } old && old.WorkspaceId == ws.Id)
        {
            var account = await accounts.GetByDeviceAsync(old.Id, ct);
            return (old, account is { Platform: Platform.Fb } ? account : null);
        }
        if (c.PreviousAccountId is { } id && await accounts.GetAsync(ws.Id, id, ct) is { Platform: Platform.Fb, IsConnected: false } free)
            return (null, free);
        return (null, null);
    }

    /// <summary>
    /// The old device of a browser that paired again goes away. Posts it had taken and never reported fail (the browser
    /// that took them is the one that just started over); the posts still waiting stay queued for the account, which
    /// the new device claims.
    /// </summary>
    private async Task ReplaceAsync(Device old, SocialAccount? kept, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var p in await posts.ListClaimedByAsync(old.Id, ct))
        {
            p.Fail(FailureCode.Network, "ส่วนขยายจับคู่ใหม่ระหว่างโพสต์", now);
            events.Add(DeviceEvents.PostChanged(old.WorkspaceId, old.Id, p, now));
        }
        foreach (var b in await bumps.ListClaimedByAsync(old.Id, ct)) b.Fail("ส่วนขยายจับคู่ใหม่ระหว่างดันโพสต์", now);
        var oldAccounts = await accounts.ListByDeviceAsync(old.Id, ct);
        foreach (var oldAcc in oldAccounts)
        {
            if (oldAcc != kept)
            {
                oldAcc.Disconnect(); // a Line account keeps its chats: the same OA reconnects to the new browser (see the heartbeat)
            }
        }
        devices.Remove(old);
        events.Add(DeviceEvents.Make(old.WorkspaceId, old.Id, DeviceEventType.Revoked, new { name = old.Name }, now));
    }

    private static string? FirstFilled(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>"k7qf 2mxp" and "K7QF-2MXP" are the same code.</summary>
    public static string NormalizeCode(string code)
    {
        var raw = new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return raw.Length == 8 ? raw[..4] + "-" + raw[4..] : raw;
    }
}

/// <param name="SessionOk">
/// True when the extension has just seen Facebook logged in again after it reported that a login was missing. A device
/// whose account waits for a login (the engine hands it nothing) is healthy again from then on, with nobody pressing
/// "sign in again" on the web. Null or false changes nothing.
/// </param>
public sealed record DeviceHeartbeatCommand(string? Version, bool? SessionOk = null, LineHeartbeatDto? Line = null, bool? LineSessionOk = null) : ICommand<DeviceStatusDto>;

public sealed class DeviceHeartbeatCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts,
    IUserRepository users, IDeviceEventRepository events, INotificationDispatcher notifier, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeviceHeartbeatCommand, DeviceStatusDto>
{
    public async Task<DeviceStatusDto> HandleAsync(DeviceHeartbeatCommand c, CancellationToken ct = default)
    {
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        var now = clock.GetUtcNow();
        if (device.Seen(c.Version, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        var pauseEnded = DevicePause.Settle(device, events, now, out var pauseReason);
        var fbAccount = await accounts.GetByDeviceAsync(device.Id, Platform.Fb, ct);
        if (c.SessionOk == true && fbAccount is { Health: AccountHealth.Relogin })
        {
            fbAccount.MarkHealthy();
            events.Add(DevicePause.Changed(device, now));
        }

        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var owner = await users.OwnerOfAsync(ws, ct);

        // A device may read several OAs (the web's LINE OA master): the one it reports now is the one this heartbeat is about.
        var deviceLines = (await accounts.ListByDeviceAsync(device.Id, ct)).Where(a => a.Platform == Platform.Line).ToList();
        SocialAccount? lineAccount = deviceLines.FirstOrDefault();
        string? lineNote = null;
        bool lineSessionLost = false;

        if (owner.HasLineChat && c.Line is { } lineReq)
        {
            if (lineReq.Ready && !string.IsNullOrWhiteSpace(lineReq.BotId))
            {
                var cleanBotId = lineReq.BotId.Trim();
                var cleanOaName = string.IsNullOrWhiteSpace(lineReq.OaName) ? cleanBotId : lineReq.OaName.Trim();

                var reported = deviceLines.FirstOrDefault(x => string.Equals(x.Handle, cleanBotId, StringComparison.OrdinalIgnoreCase));
                if (reported is not null) lineAccount = reported;
                if (reported is null)
                {
                    await uow.ExecuteInTransactionAsync($"device:line:{device.Id:N}", async () =>
                    {
                        lineAccount = (await accounts.ListByDeviceAsync(device.Id, ct))
                            .FirstOrDefault(x => x.Platform == Platform.Line && string.Equals(x.Handle, cleanBotId, StringComparison.OrdinalIgnoreCase));
                        if (lineAccount is null)
                        {
                            var existingWithHandle = await accounts.FindByHandleAsync(ws.Id, Platform.Line, cleanBotId, ct);
                            // An OA another extension still reports for is refused (nothing flips between two devices every 30 s); an OA
                            // whose browser was unbound, re-paired or has stopped reporting is taken over, chats and history included.
                            var heldByLiveDevice = false;
                            if (existingWithHandle?.DeviceId is { } holderId && holderId != device.Id)
                            {
                                var holder = await devices.GetAsync(ws.Id, holderId, ct);
                                heldByLiveDevice = holder is not null && holder.IsLineReady(now);
                            }
                            if (existingWithHandle is not null && heldByLiveDevice)
                            {
                                lineNote = "บัญชี LINE OA นี้ผูกอยู่กับเครื่องอื่นในเวิร์กสเปซแล้ว";
                            }
                            else if (existingWithHandle is not null)
                            {
                                existingWithHandle.ReattachLine(device.Id, cleanOaName);
                                lineAccount = existingWithHandle;
                                device.SetLineReady(cleanBotId, now);
                            }
                            else
                            {
                                var totalAccs = await accounts.CountAsync(ws.Id, ct);
                                lineAccount = SocialAccount.CreateLine(ws.Id, device.Id, cleanBotId, cleanOaName, now, totalAccs);
                                accounts.Add(lineAccount);
                                device.SetLineReady(cleanBotId, now);
                            }
                            // Saved INSIDE the lock: a heartbeat that waits for it must find this account, or two
                            // concurrent heartbeats of one device would each create a Line account.
                            await uow.SaveChangesAsync(ct);
                        }
                    }, ct);
                }
                else
                {
                    {
                        device.SetLineReady(cleanBotId, now);
                        var expectedName = $"LINE · {cleanOaName}";
                        if (expectedName.Length > 120) expectedName = expectedName[..120];
                        if (!string.IsNullOrWhiteSpace(lineReq.OaName) && lineAccount!.Name != expectedName) lineAccount.RenameLine(cleanOaName); // a rename never undoes a rest or a login loss
                    }
                }
            }
            else
            {
                device.ClearLineReady();
            }
        }

        if (c.LineSessionOk == true && lineAccount is { Health: AccountHealth.Relogin })
        {
            lineAccount.MarkHealthy();
            events.Add(DevicePause.Changed(device, now));
        }

        if (lineAccount is { Health: AccountHealth.Relogin })
        {
            lineSessionLost = true;
        }

        await uow.SaveChangesAsync(ct);
        if (pauseEnded) await EngineNotices.SendAsync(notifier, ws.Id, [EngineNotices.DevicePauseEnded(device, pauseReason)], ct);

        var auto = device.IsAutoPaused(now);
        return new DeviceStatusDto(
            device.Id, device.Name, ws.Id, ws.Name, fbAccount?.Id, fbAccount?.GroupLinks.Count ?? 0,
            ws.ExtensionOnline, AntiBanDto.From(ws.AntiBan), device.JobsPaused || !owner.CanPost || auto,
            auto ? device.AutoPausedUntil : null, auto ? device.AutoPauseReason : null,
            fbAccount is { CanPost: false },
            LineSessionLost: lineSessionLost,
            LineAccountId: lineAccount?.Id,
            LineNote: lineNote);
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

public sealed record SyncLineChatDto(string UserId, string Name, IReadOnlyList<string>? Labels = null, DateTimeOffset? LastMessageAt = null, bool? Unread = null);

public sealed record SyncLineChatsCommand(string BotId, string OaName, bool Complete, IReadOnlyList<SyncLineChatDto> Chats) : ICommand<int>;

public sealed class SyncLineChatsCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts,
    ILineChatRepository lineChats, IDeviceEventRepository events, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SyncLineChatsCommand, int>
{
    public async Task<int> HandleAsync(SyncLineChatsCommand c, CancellationToken ct = default)
    {
        var device = await DeviceAccess.RequireAsync(devices, current, ct);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("ไม่พบเวิร์กสเปซ", device.WorkspaceId);
        await users.RequireLineChatAsync(ws, ct);
        var account = (await accounts.ListByDeviceAsync(device.Id, ct))
            .FirstOrDefault(x => x.Platform == Platform.Line && string.Equals(x.Handle, c.BotId?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("อุปกรณ์นี้ยังไม่ได้ผูกบัญชี LINE OA นี้ (เพิ่มในหน้า LINE OA ก่อน)");
        var now = clock.GetUtcNow();

        if (device.Seen(null, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));
        // The roster belongs to the OA this device is attached to: a list for another OA is refused, never merged in.
        if (!string.Equals(account.Handle, c.BotId?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DomainException("รายชื่อแชทนี้เป็นของ LINE OA อื่น ไม่ตรงกับ LINE OA ที่เครื่องนี้ผูกอยู่");

        // The extension scrolls a list that may repeat a row: the last one of each customer wins. Labels are kept short.
        var rows = c.Chats
            .Where(x => LineChatUrl.IsValidUserId(x.UserId))
            .GroupBy(x => x.UserId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        var incoming = rows.Select(x => LineChat.Create(
            device.WorkspaceId,
            account.Id,
            x.UserId,
            LineChatUrl.Build(account.Handle, x.UserId),
            x.Name,
            (x.Labels ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().Length > 60 ? l.Trim()[..60] : l.Trim()).Distinct().Take(20).ToList(),
            x.LastMessageAt,
            x.Unread ?? false,
            now)).ToList();

        int total = 0;
        await uow.ExecuteInTransactionAsync($"line-chats:{account.Id:N}", async () =>
        {
            var before = await lineChats.CountByAccountAsync(account.Id, ct);
            // An empty "complete" list is a failed scroll, not a customer base that vanished: it removes nothing.
            await lineChats.ReplaceForAccountAsync(account.Id, incoming, c.Complete && incoming.Count > 0, now, ct);
            await uow.SaveChangesAsync(ct);
            total = await lineChats.CountByAccountAsync(account.Id, ct);
            if (total != before || incoming.Count > 0)
                events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.LineChats, new { count = total }, now));
            await uow.SaveChangesAsync(ct);
        }, ct);
        return total;
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
    IPostRepository posts, IMediaRepository media, IUserRepository users, IPlanRepository plans, ISetLinkRepository links, ILinkSetRepository linkSets,
    IScheduleRepository schedules, ICollectionRepository collections, IPostBumpRepository bumps, IDeviceEventRepository events,
    ScheduleTopUp topUp, INotificationDispatcher notifier, IUnitOfWork uow, TimeProvider clock, ILineChatRepository? lineChats = null)
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
        Device? taker = null;
        var notes = new Notes();
        try
        {
            // One claim of a device at a time: the lock is held until the commit, so two overlapping claims (a retry, a
            // slow answer) cannot both hand out the same post or two posts at once. A post that still changes under a claim
            // (the web app edited it) fails the save with a conflict instead.
            await uow.ExecuteInTransactionAsync($"claim:{current.DeviceId:N}", async () =>
            {
                uow.DiscardChanges(); // what was loaded before the lock (the device at sign-in) may be out of date
                var device = taker = await DeviceAccess.RequireAsync(devices, current, ct);
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
                notices.AddRange(await EngineNotices.FinishedRoundsAsync(posts, schedules, ws!.Id, notes.Slots, now, ct));
            }
            catch (Exception)
            {
                // A round summary is a courtesy.
            }
        }
        if (taker is not null) notices.InsertRange(0, await JobNoticesAsync(ws!, taker, notes, now, ct));
        await EngineNotices.SendAsync(notifier, ws!.Id, notices, ct);
        return job;
    }

    /// <summary>
    /// The messages about the jobs themselves: a job lost (taken, never reported), posts skipped for being too late, the
    /// device's rest being over, and the job this claim handed out. Only worked out for messages somebody would get.
    /// </summary>
    private async Task<List<Notice>> JobNoticesAsync(Workspace ws, Device device, Notes notes, DateTimeOffset now, CancellationToken ct)
    {
        var notices = new List<Notice>();
        var n = ws.Notifications;
        if (!n.Telegram.IsReady && !n.Line.IsReady) return notices; // nowhere to send: no lookups for nothing
        try
        {
            if (notes.PauseEnded) notices.Add(EngineNotices.DevicePauseEnded(device, notes.PauseReason));
            if (notes.LateSkipped > 0) notices.Add(EngineNotices.LateSkipped(notes.LateSkipped, device, notes.LateDetail!));
            foreach (var lost in notes.Lost)
            {
                var setId = await LinkSetIdAsync(ws.Id, lost, ct);
                if (!n.Resolve(NotifyEvent.Fail, setId, lost.LinkId).Any) continue;
                notices.Add(EngineNotices.JobLost(lost, setId, device, await EngineNotices.ContextAsync(posts, schedules, linkSets, ws.Id, lost, setId, now, ct)));
            }
            if (notes.Taken is { } taken)
            {
                var setId = await LinkSetIdAsync(ws.Id, taken, ct);
                if (n.Resolve(NotifyEvent.Job, setId, taken.LinkId).Any)
                    notices.Add(EngineNotices.JobTaken(taken, setId, device, await EngineNotices.ContextAsync(posts, schedules, linkSets, ws.Id, taken, setId, now, ct), now));
            }
            if (notes.TakenBump is { } bump && n.Resolve(NotifyEvent.Job, null, null).Any)
            {
                var name = bump.ScheduleId is { } sid ? (await schedules.GetAsync(ws.Id, sid, ct))?.Name : null;
                notices.Add(EngineNotices.BumpTaken(bump, device, name, now));
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Telling people is a courtesy: the claim already happened.
        }
        return notices;
    }

    private async Task<Guid?> LinkSetIdAsync(Guid workspaceId, Post p, CancellationToken ct) =>
        p.LinkId is { } id ? (await links.GetAsync(workspaceId, id, ct))?.LinkSetId : null;

    /// <summary>What a claim settled on the way, for the messages sent afterwards.</summary>
    private sealed class Notes
    {
        public int QuotaFailed { get; private set; }
        public string? QuotaDetail { get; private set; }
        public HashSet<(Guid ScheduleId, string SlotKey)> Slots { get; } = [];
        /// <summary>Posts the device took and never reported on: failed by this claim.</summary>
        public List<Post> Lost { get; } = [];
        public int LateSkipped { get; private set; }
        public string? LateDetail { get; private set; }
        public bool PauseEnded { get; set; }
        public string? PauseReason { get; set; }
        /// <summary>The post (or bump) this claim handed out.</summary>
        public Post? Taken { get; set; }
        public PostBump? TakenBump { get; set; }

        public void Late(string detail)
        {
            LateSkipped++;
            LateDetail ??= detail;
        }

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

    /// <summary>Plan and all-platform daily numbers shared by the accounts of one claim.</summary>
    private sealed record Quotas(int? PlanPosts, int SentByOwner, int SentAll);

    /// <summary>What one account offers: a post to hand out, or nothing (GapBlocked: it is still inside the anti-ban gap).</summary>
    private sealed record Pick(Candidate? Candidate, bool GapBlocked = false);

    private sealed record Candidate(
        Post Post,
        SocialAccount Account,
        string Url,
        Guid? SetId,
        Schedule? Schedule,
        DateTimeOffset DueAt);

    private async Task<JobDto?> NextAsync(Device device, Workspace ws, DateTimeOffset now, Notes notes, CancellationToken ct)
    {
        var claimed = await posts.ListClaimedByAsync(device.Id, ct);
        foreach (var p in claimed.Where(p => p.ClaimExpired(now)))
        {
            p.Fail(FailureCode.Network, "ไม่ได้รับผลการโพสต์จากส่วนขยายภายใน 15 นาที", now);
            events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
            notes.Settled(p);
            notes.Lost.Add(p);
        }
        if (claimed.Any(p => !p.ClaimExpired(now))) return null;

        var claimedBumps = await bumps.ListClaimedByAsync(device.Id, ct);
        foreach (var b in claimedBumps.Where(b => b.ClaimExpired(now))) b.Fail("ไม่ได้รับผลการดันโพสต์จากส่วนขยายภายใน 15 นาที", now);
        if (claimedBumps.Any(b => !b.ClaimExpired(now))) return null;

        notes.PauseEnded = DevicePause.Settle(device, events, now, out var pauseReason);
        notes.PauseReason = pauseReason;
        if (device.JobsPaused || device.IsAutoPaused(now) || !ws.ExtensionOnline) return null;
        var owner = await users.OwnerOfAsync(ws, ct);
        if (!owner.CanPost) return null;

        var deviceAccounts = await accounts.ListByDeviceAsync(device.Id, ct);
        if (deviceAccounts.Count == 0) return null;

        // The shared daily numbers (plan, all-platform cap) cost several queries: only worked out once some account has a due post.
        Quotas? quotas = null;
        async Task<Quotas> QuotasAsync()
        {
            if (quotas is not null) return quotas;
            var plan = (await plans.ForAsync(owner, ct)).Posts;
            var ownWorkspaces = (await workspaces.ListByOwnerAsync(owner.Id, ct)).Select(w => w.Id).ToList();
            var byOwner = plan is null ? 0 : await posts.CountPublishedSinceAsync(ownWorkspaces, now.AddDays(-1), ct);
            var all = ws.AntiBan.Advanced.DailyAll > 0 ? await posts.CountPublishedInWorkspaceSinceAsync(ws.Id, now.AddDays(-1), ct) : 0;
            return quotas = new Quotas(plan, byOwner, all);
        }

        var candidates = new List<Candidate>();
        var fbGapBlocked = false;
        foreach (var acc in deviceAccounts)
        {
            if (!acc.CanPost || acc.IsResting(now)) continue;
            // A browser works for ONE OA at a time (the one chosen on the web); the chats of the others wait until it is switched.
            if (acc.Platform == Platform.Line
                && (!device.IsLineReady(now) || !owner.HasLineChat
                    || !string.Equals(device.LineBotId, acc.Handle, StringComparison.OrdinalIgnoreCase))) continue;

            var pick = await FindCandidateAsync(device, ws, acc, owner, QuotasAsync, now, notes, ct);
            if (pick.Candidate is not null) candidates.Add(pick.Candidate);
            if (acc.Platform == Platform.Fb && pick.GapBlocked) fbGapBlocked = true;
        }

        if (candidates.Count > 0)
        {
            var winner = candidates
                .OrderBy(c => c.DueAt)
                .ThenBy(c => c.Account.Platform == Platform.Fb ? 0 : 1)
                .First();

            winner.Post.Claim(device.Id, now);
            notes.Taken = winner.Post;

            var files = (await media.ListAsync(ws.Id, ct)).ToDictionary(f => f.Id);
            var items = winner.Post.MediaIds
                .Where(files.ContainsKey)
                .Select(id => new JobMediaDto(id, files[id].Name, files[id].ContentType))
                .ToList();

            if (winner.Account.Platform == Platform.Line)
            {
                var lineOpts = winner.Schedule?.EffectiveLineOptions ?? new LineOptions();
                var bubbles = PostComposer.SplitBubbles(winner.Post.Content, lineOpts.SplitBubbles);
                var lineDto = new LineJobOptionsDto(lineOpts.SkipIfActiveMinutes, lineOpts.ImagesFirst, lineOpts.AllowedFrom, lineOpts.AllowedTo);
                return new JobDto(
                    winner.Post.Id, winner.Post.Target, winner.Url, winner.Post.Content, items,
                    AntiBanDto.From(ws.AntiBan), "post", "line", "",
                    Shot: false, Channel: "line", Bubbles: bubbles, Line: lineDto);
            }
            else
            {
                var pageTags = "";
                if (winner.Schedule is not null)
                {
                    pageTags = (await collections.GetAsync(ws.Id, winner.Schedule.CollectionId, ct))?.Settings.PageTags ?? "";
                }
                var kind = FacebookGroupUrl.KindOf(winner.Url) == FacebookTargetKind.Page ? "page" : "group";
                return new JobDto(
                    winner.Post.Id, winner.Post.Target, winner.Url, winner.Post.Content, items,
                    AntiBanDto.From(ws.AntiBan), "post", kind, pageTags,
                    Shot: await WantsShotAsync(ws, owner, winner.Post, winner.SetId, ct),
                    Channel: "fb");
            }
        }

        // A bump is a Facebook comment: it waits like a post for the anti-ban gap (a comment right after a post is as fast as two posts).
        var fbAcc = deviceAccounts.FirstOrDefault(a => a.Platform == Platform.Fb && a.CanPost && !a.IsResting(now));
        if (fbAcc is not null && !fbGapBlocked)
        {
            return await NextBumpAsync(device, ws, fbAcc, owner, now, notes, ct);
        }

        return null;
    }

    private async Task<Pick> FindCandidateAsync(
        Device device, Workspace ws, SocialAccount account, User owner,
        Func<Task<Quotas>> quotasAsync,
        DateTimeOffset now, Notes notes, CancellationToken ct)
    {
        var advanced = ws.AntiBan.Advanced;
        TimeSpan gap;
        DateTimeOffset? last;
        if (account.Platform == Platform.Line)
        {
            gap = TimeSpan.FromSeconds(ws.AntiBan.LineChat.MinGapSeconds);
            last = await posts.LastPublishedAtAsync(account.Id, ct);
        }
        else
        {
            gap = TimeSpan.FromMinutes(Math.Max(ws.AntiBan.Min, advanced.MinGap));
            var lastPosted = await posts.LastPublishedAtAsync(account.Id, ct);
            var lastBump = await bumps.LastDoneAtAsync(account.Id, ct);
            last = lastPosted is { } lp0 && lastBump is { } lb ? (lp0 > lb ? lp0 : lb) : lastPosted ?? lastBump;
        }

        if (last is { } l && now - l < gap) return new Pick(null, GapBlocked: true);

        var due = await posts.ListDueAsync(account.Id, now, ct);
        if (due.Count == 0) return new Pick(null);

        // Too many of the last day's posts failed ON THIS PLATFORM: its account stops until they age out (or the numbers are
        // changed). Per platform, so a noisy LINE channel never halts Facebook posting (decision D12) and the reverse.
        if (advanced.StopFailPct > 0)
        {
            var (finished, failed) = await posts.CountOutcomesSinceAsync(ws.Id, now.AddDays(-1), account.Platform, ct);
            if (finished >= MinFinishedForHalt && failed * 100 > advanced.StopFailPct * finished) return new Pick(null);
        }
        var shared = await quotasAsync();
        var planPosts = shared.PlanPosts;
        var sentByOwner = shared.SentByOwner;
        var sentAll = shared.SentAll;

        var limit = ws.AntiBan.Limits.For(account.Platform);
        var warmedUp = ws.AntiBan.Warmup && WarmupLimit(now - device.CreatedAt) < limit;
        if (ws.AntiBan.Warmup) limit = Math.Min(limit, WarmupLimit(now - device.CreatedAt));
        var sentToday = await posts.CountPublishedSinceAsync(ws.Id, account.Platform, now.AddDays(-1), ct);

        if (account.Platform == Platform.Line && sentToday >= limit) return new Pick(null);

        var openFrom = last is { } lp ? lp + gap : DateTimeOffset.MinValue;

        var linkCache = new Dictionary<Guid, SetLink?>();
        var perLinkDay = new Dictionary<(Guid Link, DateTimeOffset Day), int>();
        var scheduleCache = new Dictionary<Guid, Schedule?>();
        var lastToLink = new Dictionary<Guid, DateTimeOffset?>();
        DateTimeOffset? lastBegan = null;
        var lastBeganKnown = false;

        async Task<Schedule?> ScheduleOfAsync(Guid scheduleId)
        {
            if (!scheduleCache.TryGetValue(scheduleId, out var found))
                scheduleCache[scheduleId] = found = await schedules.GetAsync(ws.Id, scheduleId, ct);
            return found;
        }

        async Task<(DateTimeOffset Start, DateTimeOffset End)> DayOfAsync(Post p)
        {
            var offset = p.ScheduleId is { } scheduleId ? (await ScheduleOfAsync(scheduleId))?.UtcOffsetMinutes ?? 0 : 0;
            return Schedule.LocalDayBounds(p.ScheduledAt, offset);
        }

        void Settle(Post p)
        {
            events.Add(DeviceEvents.PostChanged(ws.Id, device.Id, p, now));
            notes.Settled(p);
        }

        IReadOnlyList<SocialAccount>? accountList = null;
        OwnershipMap? ownership = null;
        var setCache = new Dictionary<Guid, LinkSet?>();
        async Task<string?> WrongMachineAsync(SetLink link)
        {
            accountList ??= await accounts.ListAsync(ws.Id, ct);
            if (!setCache.TryGetValue(link.LinkSetId, out var set)) setCache[link.LinkSetId] = set = await linkSets.GetAsync(ws.Id, link.LinkSetId, ct);
            if (set is null) return null;
            var setOwner = LinkOwnership.OwnerOf(set, accountList);
            if (setOwner is null)
                return link.Channel == Platform.Line
                    ? $"ชุดลิงก์ \"{set.Name}\" ยังไม่ได้เลือกเครื่องที่จะส่ง จึงไม่ส่ง (เลือกเครื่องในการ์ดของชุดลิงก์)"
                    : $"ชุดลิงก์ \"{set.Name}\" ยังไม่ได้เลือกเครื่องที่จะโพสต์ จึงไม่โพสต์ (เลือกเครื่องในการ์ดของชุดลิงก์)";
            if (setOwner != account.Id)
                return link.Channel == Platform.Line
                    ? $"ชุดลิงก์ \"{set.Name}\" ใช้เครื่อง \"{OwnershipMap.ExtensionName(setOwner.Value, accountList)}\" ไม่ใช่เครื่องนี้ จึงไม่ส่ง"
                    : $"ชุดลิงก์ \"{set.Name}\" ใช้เครื่อง \"{OwnershipMap.ExtensionName(setOwner.Value, accountList)}\" ไม่ใช่เครื่องนี้ จึงไม่โพสต์";
            ownership ??= OwnershipMap.Build(await linkSets.ListAsync(ws.Id, ct), await links.ListAsync(ws.Id, ct), accountList);
            return ownership.WinnerOf(link.Url) is { } first && first.Owner != account.Id
                ? (link.Channel == Platform.Line
                    ? $"แชทนี้อยู่ในชุดลิงก์ \"{first.Set.Name}\" ของเครื่อง \"{OwnershipMap.ExtensionName(first.Owner, accountList)}\" ด้วย (แชทหนึ่งใช้ได้กับเครื่องเดียว) จึงไม่ส่งจากเครื่องนี้"
                    : $"กลุ่มนี้อยู่ในชุดลิงก์ \"{first.Set.Name}\" ของเครื่อง \"{OwnershipMap.ExtensionName(first.Owner, accountList)}\" ด้วย (กลุ่มหนึ่งใช้ได้กับเครื่องเดียว) จึงไม่โพสต์จากเครื่องนี้")
                : null;
        }

        foreach (var p in due)
        {
            if (p.ScheduleId is not null && p.LinkId is null && !p.IsTest && p.TargetKey is { } key && key.StartsWith("account:", StringComparison.Ordinal))
            {
                p.Skip(now, "ชุดลิงก์ไม่โพสต์ผ่านเครื่องของบัญชีอื่นอีกแล้ว (ชุดหนึ่งใช้เครื่องเดียว) จึงไม่โพสต์");
                Settle(p);
                continue;
            }

            Schedule? sch = null;
            if (p.ScheduleId is { } sid)
            {
                sch = await ScheduleOfAsync(sid);
            }

            if (account.Platform == Platform.Line && lineChats is not null && !string.IsNullOrWhiteSpace(p.TargetUrl))
            {
                var userId = LineChatUrl.UserId(p.TargetUrl);
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    var chat = await lineChats.FindAsync(account.Id, userId, ct);
                    if (chat is { DoNotSend: true })
                    {
                        p.Skip(now, "ลูกค้านี้ถูกตั้งเป็นห้ามส่ง");
                        Settle(p);
                        continue;
                    }
                }
            }

            SetLink? link = null;
            var holdUntil = DateTimeOffset.MinValue;
            if (account.Platform == Platform.Line && !p.IsTest && sch is not null && !sch.AllowsLineAt(now))
            {
                // Quiet hours (decision D11b): outside the schedule's allowed hours the post waits for the window to open, and
                // the hours it spent waiting do not make it late. Other schedules' posts are still looked at.
                holdUntil = Latest(holdUntil, sch.NextLineWindowOpen(now), DateTimeOffset.MinValue);
            }
            Guid? setOfPost = null;
            if (p.LinkId is { } linkId)
            {
                if (!linkCache.TryGetValue(linkId, out link)) linkCache[linkId] = link = await links.GetAsync(ws.Id, linkId, ct);
                if (link is null || !link.Enabled)
                {
                    var noun = link?.Channel == Platform.Line ? "แชท" : "กลุ่ม";
                    p.Skip(now, $"{noun}ถูกปิดหรือถูกลบแล้ว");
                    Settle(p);
                    continue;
                }
                setOfPost = link.LinkSetId;
                if (!p.IsTest && await WrongMachineAsync(link) is { } wrong)
                {
                    p.Skip(now, wrong);
                    Settle(p);
                    continue;
                }
                if (!setCache.TryGetValue(link.LinkSetId, out var set)) setCache[link.LinkSetId] = set = await linkSets.GetAsync(ws.Id, link.LinkSetId, ct);
                var dailyCap = set?.DailyCapFor(link) ?? link.DailyMax;
                if (dailyCap > 0)
                {
                    var (dayStart, dayEnd) = await DayOfAsync(p);
                    if (!perLinkDay.TryGetValue((linkId, dayStart), out var published))
                        perLinkDay[(linkId, dayStart)] = published = await posts.CountPublishedToLinkAsync(linkId, dayStart, dayEnd, ct);
                    if (published >= dailyCap)
                    {
                        var noun = link.Channel == Platform.Line ? "แชท" : "กลุ่ม";
                        p.Skip(now, $"ครบเพดานต่อวันของ{noun}นี้");
                        Settle(p);
                        continue;
                    }
                }
                if (advanced.Cooldown > 0 && account.Platform == Platform.Fb)
                {
                    if (!lastToLink.TryGetValue(linkId, out var lastPost))
                        lastToLink[linkId] = lastPost = await posts.LastPublishedToLinkAtAsync(linkId, ct);
                    if (lastPost is { } lastAt) holdUntil = lastAt + TimeSpan.FromHours(advanced.Cooldown);
                }
            }
            else if (p.ScheduleId is not null && sch is not null)
            {
                setOfPost = sch.LinkSetId;
            }

            if (p.ScheduleId is { } gapScheduleId && p.RushedAt is null && !p.IsTest
                && (sch ?? await ScheduleOfAsync(gapScheduleId)) is { GroupGapMinutes: > 0 } gapSchedule)
            {
                if (!lastBeganKnown)
                {
                    lastBegan = await posts.LastClaimedAtAsync(account.Id, ct);
                    lastBeganKnown = true;
                }
                if (lastBegan is { } began) holdUntil = Latest(holdUntil, began + TimeSpan.FromMinutes(gapSchedule.GroupGapMinutes), DateTimeOffset.MinValue);
            }

            if (now - Latest(p.ScheduledAt, openFrom, holdUntil) > ws.Offline.MaxLateness)
            {
                p.SkipLate(now);
                Settle(p);
                notes.Late("งานเหล่านี้ถึงเวลาตอนเครื่องไม่ได้รับงานนานเกินกำหนด (ตั้งไว้ที่หน้านโยบายออฟไลน์) จึงไม่โพสต์ย้อนหลัง");
                continue;
            }
            if (holdUntil > now) continue;

            if (sentToday >= limit)
            {
                if (account.Platform == Platform.Line)
                {
                    // A LINE post over the 24 h cap is not failed: it waits (the lateness rule skips it if it waits too long).
                    return new Pick(null);
                }
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

            var url = p.TargetUrl ?? account.UrlFor(p.Target);
            if (url is null)
            {
                var noun = account.Platform == Platform.Line ? "แชท" : "กลุ่ม";
                p.Fail(FailureCode.Network, $"ไม่พบลิงก์ของ{noun} \"{p.Target}\" ในส่วนขยาย", now);
                Settle(p);
                continue;
            }

            return new Pick(new Candidate(p, account, url, setOfPost, sch, p.RushedAt ?? p.ScheduledAt));
        }

        return new Pick(null);
    }

    /// <summary>
    /// The next comment that bumps a post of this account, or null. Only for an owner whose plan includes bumping; one that
    /// is long overdue is dropped (a bump a day late is no bump), and so is one whose schedule is gone.
    /// </summary>
    /// <summary>
    /// Whether the extension should take a picture of the posting window for this post: somebody would get the message
    /// about it on Telegram, with a screenshot (the "screenshot" event is on for that group). Saves the picture
    /// (a few hundred KB) from being taken and sent when nobody would see it.
    /// </summary>
    private async Task<bool> WantsShotAsync(Workspace ws, User owner, Post p, Guid? setId, CancellationToken ct)
    {
        if (!owner.HasNotifications) return false;
        var n = ws.Notifications;
        setId ??= p.LinkId is { } lid ? (await links.GetAsync(ws.Id, lid, ct))?.LinkSetId : null;
        return n.Resolve(NotifyEvent.Shot, setId, p.LinkId).Telegram
            && (n.Resolve(NotifyEvent.Success, setId, p.LinkId).Telegram
                || n.Resolve(NotifyEvent.Fail, setId, p.LinkId).Telegram
                || n.Resolve(NotifyEvent.Block, setId, p.LinkId).Telegram);
    }

    private async Task<JobDto?> NextBumpAsync(Device device, Workspace ws, SocialAccount account, User owner, DateTimeOffset now, Notes notes, CancellationToken ct)
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
            notes.TakenBump = b;
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
/// <param name="Shot">A picture of the posting window (already checked, see <see cref="ShotImage"/>): goes to Telegram with the message about this post.</param>
public sealed record ReportJobResultCommand(
    Guid PostId, bool Ok, bool AwaitingApproval, bool NeedsLogin, bool Blocked, string? Error,
    string? PostUrl = null, byte[]? Shot = null,
    bool Skip = false, int? DeferMinutes = null, string? Reason = null) : ICommand<PostDto>;

/// <summary>
/// Settles a post the device reported on, and what follows from it: the link's health (a group that kept failing is
/// switched off), the device's pause (Facebook blocked it, or too many posts failed in a row), and the messages to
/// Telegram/LINE. Failures caused by the account (a lost login, a Facebook block) say nothing about the group, so they
/// do not count against the link.
/// </summary>
public sealed class ReportJobResultCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts, IPostRepository posts,
    ISetLinkRepository links, ILinkSetRepository linkSets, IScheduleRepository schedules, IUserRepository users, IPostBumpRepository bumps,
    IDeviceEventRepository events, INotificationDispatcher notifier, IRandomSource random, IUnitOfWork uow, TimeProvider clock)
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
        var account = await accounts.GetAsync(device.WorkspaceId, post.AccountId, ct);
        var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", device.WorkspaceId);
        var advanced = ws.AntiBan.Advanced;
        var link = post.LinkId is { } linkId ? await links.GetAsync(ws.Id, linkId, ct) : null;
        var notices = new List<Notice>();
        var where = await NoticeContextAsync(ws, post, link?.LinkSetId, c, now, ct);

        if (c.Skip)
        {
            var skipReason = string.IsNullOrWhiteSpace(c.Reason) ? "ข้ามการส่งแชทนี้" : c.Reason;
            post.SkipClaimed(skipReason, now);
            if (link is not null)
            {
                link.DisableManually();
                events.Add(DeviceEvents.LinksChanged(ws.Id, device.Id, link.LinkSetId, link.Id, link.Health, now));
            }
            notices.Add(EngineNotices.ChatSkipped(post, link?.LinkSetId, skipReason, where));
        }
        else if (c.DeferMinutes is { } deferM && deferM > 0)
        {
            if (post.DeferCount >= 6)
            {
                // A customer who is always mid-conversation is not an unusable chat: skip this post, keep the link on.
                post.SkipClaimed("ลูกค้าคุยอยู่ตลอด จึงข้ามแชทนี้", now);
                notices.Add(EngineNotices.ChatSkipped(post, link?.LinkSetId, "ลูกค้าคุยอยู่ตลอด จึงข้ามแชทนี้", where));
            }
            else
            {
                var jitter = random.NextDouble() * 0.4 - 0.2;
                var deferSecs = (int)Math.Round(deferM * 60 * (1 + jitter));
                post.Defer(now.AddSeconds(deferSecs), now);
            }
        }
        else if (c.Ok)
        {
            post.CompletePosted(c.AwaitingApproval, now);
            if (!c.AwaitingApproval && post.Platform == Platform.Fb)
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
            notices.Add(EngineNotices.Posted(post, link?.LinkSetId, c.AwaitingApproval, where, now));
        }
        else
        {
            var code = c.NeedsLogin ? FailureCode.Session : c.Blocked ? FailureCode.RateLimit : FailureCode.Network;
            post.Fail(code, c.Error, now);
            if (c.NeedsLogin) account?.MarkNeedsLogin();

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
                var pauseText = post.Platform == Platform.Line
                    ? "LINE แจ้งเตือน ระบบพักเครื่องชั่วคราว"
                    : "Facebook ขัดขวางการโพสต์ ระบบพักเครื่องชั่วคราว";
                if (device.AutoPause(now + blockPause.Value, pauseText, now))
                    events.Add(DevicePause.Changed(device, now));
            }
            if (c.NeedsLogin) notices.Add(EngineNotices.NeedsLogin(post, link?.LinkSetId, device, where));
            else if (c.Blocked) notices.Add(EngineNotices.Blocked(post, link?.LinkSetId, device, c.Error, blockPause, where));
            else notices.Add(EngineNotices.Failed(post, link?.LinkSetId, c.Error, where));
            if (switchedOff is not null) notices.Add(switchedOff);

            if (advanced.FailStreak > 0 && account is not null)
            {
                var before = advanced.FailStreak > 1 ? await posts.ListRecentOutcomesAsync(account.Id, advanced.FailStreak - 1, ct) : [];
                if (before.Count == advanced.FailStreak - 1 && before.All(s => s == PostStatus.Failed))
                {
                    var rest = TimeSpan.FromHours(StreakPauseMinHours + random.NextDouble() * (StreakPauseMaxHours - StreakPauseMinHours));
                    if (account.Platform == Platform.Line)
                    {
                        account.Rest(now + rest);
                        notices.Add(EngineNotices.FailStreak(device, advanced.FailStreak, rest, Platform.Line));
                    }
                    else
                    {
                        if (device.AutoPause(now + rest, $"โพสต์ล้มเหลวติดกัน {advanced.FailStreak} ครั้ง ระบบพักเครื่องชั่วคราว", now))
                        {
                            events.Add(DevicePause.Changed(device, now));
                            notices.Add(EngineNotices.FailStreak(device, advanced.FailStreak, rest, Platform.Fb));
                        }
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
                notices.AddRange(await EngineNotices.FinishedRoundsAsync(posts, schedules, ws.Id, [(scheduleId, slot)], now, ct));
            }
            catch (Exception)
            {
                // A round summary is a courtesy.
            }
        }
        // The picture belongs to the message about this post, which is always the first one.
        // A LINE window shows customers' messages: its picture is never forwarded, whatever the device sent.
        if (c.Shot is not null && post.Platform != Platform.Line && notices.Count > 0) notices[0] = notices[0] with { Photo = c.Shot };
        await EngineNotices.SendAsync(notifier, ws.Id, notices, ct);
        return PostDto.From(post);
    }

    /// <summary>
    /// Where the post stands in its round, for the message about it. Only looked up when somebody would get that message,
    /// and a failure here only leaves the message without those lines.
    /// </summary>
    private async Task<PostContext> NoticeContextAsync(Workspace ws, Post post, Guid? linkSetId, ReportJobResultCommand c, DateTimeOffset now, CancellationToken ct)
    {
        var ev = c.Ok ? NotifyEvent.Success : c.NeedsLogin || c.Blocked ? NotifyEvent.Block : NotifyEvent.Fail;
        if (!ws.Notifications.Resolve(ev, linkSetId, post.LinkId).Any) return PostContext.None;
        try
        {
            return await EngineNotices.ContextAsync(posts, schedules, linkSets, ws.Id, post, linkSetId, now, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return PostContext.None;
        }
    }
}

/// <summary>The extension's report on a bump it took: the comment was made, or why not.</summary>
public sealed record ReportBumpResultCommand(Guid BumpId, bool Ok, bool NeedsLogin, bool Blocked, string? Error) : ICommand<Unit>;

public sealed class ReportBumpResultCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IWorkspaceRepository workspaces, IAccountRepository accounts,
    IPostBumpRepository bumps, IScheduleRepository schedules, IDeviceEventRepository events, INotificationDispatcher notifier, IRandomSource random,
    IUnitOfWork uow, TimeProvider clock)
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
        await NotifyAsync(device, bump, c, ct);
        return Unit.Value;
    }

    // Only for a workspace that has the message switched on (it looks the schedule up), and never a failure of the result.
    private async Task NotifyAsync(Device device, PostBump bump, ReportBumpResultCommand c, CancellationToken ct)
    {
        try
        {
            var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct);
            var ev = c.Ok ? NotifyEvent.Job : c.NeedsLogin || c.Blocked ? NotifyEvent.Block : NotifyEvent.Fail;
            if (ws is null || !ws.Notifications.Resolve(ev, null, null).Any) return;
            var name = bump.ScheduleId is { } sid ? (await schedules.GetAsync(ws.Id, sid, ct))?.Name : null;
            await EngineNotices.SendAsync(notifier, ws.Id, [EngineNotices.BumpDone(bump, device, c.Ok, c.NeedsLogin, c.Blocked, c.Error, name)], ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A courtesy: the result is already saved.
        }
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

    /// <summary>The pause ran out: clears it and says so, once. Returns true (and why it had paused) when there was one to end.</summary>
    public static bool Settle(Device device, IDeviceEventRepository events, DateTimeOffset now, out string? reason)
    {
        reason = device.AutoPauseReason;
        if (!device.EndAutoPause(now)) return false;
        events.Add(Changed(device, now));
        return true;
    }
}
