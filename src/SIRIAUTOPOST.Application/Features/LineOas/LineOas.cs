using System.Text.Json;
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

namespace SIRIAUTOPOST.Application.Features.LineOas;

/// <summary>Builds the rows of the master list (one Line account per OA) with the numbers the page shows.</summary>
internal static class LineOaRows
{
    public static async Task<IReadOnlyList<LineOaDto>> BuildAsync(
        Guid workspaceId, IAccountRepository accounts, IDeviceRepository devices, ILineChatRepository lineChats,
        ILinkSetRepository sets, DateTimeOffset now, CancellationToken ct)
    {
        var lines = (await accounts.ListAsync(workspaceId, ct)).Where(a => a.Platform == Platform.Line).ToList();
        var deviceById = (await devices.ListAsync(workspaceId, ct)).ToDictionary(d => d.Id);
        var allSets = await sets.ListAsync(workspaceId, ct);
        var rows = new List<LineOaDto>();
        foreach (var a in lines)
        {
            Device? d = a.DeviceId is { } id && deviceById.TryGetValue(id, out var dev) ? dev : null;
            rows.Add(new LineOaDto(
                a.Id, a.Name, a.Handle, LineOaUrl.Build(a.Handle), a.DeviceId, d?.Name, d?.IsOnline(now) ?? false,
                d is not null && d.IsLineReady(now) && string.Equals(d.LineBotId, a.Handle, StringComparison.OrdinalIgnoreCase),
                await lineChats.CountByAccountAsync(a.Id, ct), await lineChats.MaxSyncedAtAsync(a.Id, ct),
                a.Health == AccountHealth.Relogin, allSets.Count(s => s.PostAsAccountId == a.Id),
                d is not null && string.Equals(d.LineBotId, a.Handle, StringComparison.OrdinalIgnoreCase)));
        }
        return rows;
    }
}

internal static class LineOaCommands
{
    /// <summary>
    /// The browser that works for an OA. An OA whose browser was unbound or changed ("change LINE OA") has none; with ONE
    /// browser in the workspace it simply goes back to it, with several the person must say which (removing and adding the OA
    /// again asks for it).
    /// </summary>
    public static async Task<Guid> DeviceOfAsync(SocialAccount a, IDeviceRepository devices, Guid workspaceId, CancellationToken ct)
    {
        if (a.DeviceId is { } id) return id;
        var list = await devices.ListAsync(workspaceId, ct);
        if (list.Count == 0) throw new DomainException("ยังไม่มีส่วนขยายที่เชื่อมไว้: เชื่อมส่วนขยายก่อน แล้วค่อยใช้ LINE OA นี้");
        if (list.Count > 1)
            throw new DomainException("LINE OA นี้ยังไม่ได้ผูกกับเครื่องใด และมีส่วนขยายหลายเครื่อง: ลบ OA นี้แล้วเพิ่มใหม่พร้อมเลือกเครื่อง");
        a.ReattachLine(list[0].Id, a.Name.StartsWith("LINE · ") ? a.Name["LINE · ".Length..] : a.Name);
        return list[0].Id;
    }

    /// <summary>Queues the "read this OA's customers" command for a browser (the extension opens the OA and scrolls its chat list).</summary>
    public static void Ask(IExtensionRepository ext, IDeviceEventRepository events, Guid workspaceId, Guid deviceId, string botId, DateTimeOffset now, string cmdName = "syncLineChats")
    {
        var cmd = DeviceCommand.Create(workspaceId, deviceId, cmdName, JsonSerializer.Serialize(new { botId }), now);
        ext.Add(cmd);
        events.Add(CommandEvents.Of(cmd, now));
    }
}

public sealed record GetLineOasQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<LineOaDto>>;

public sealed class GetLineOasQueryHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts, IDeviceRepository devices,
    ILineChatRepository lineChats, ILinkSetRepository sets, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetLineOasQuery, IReadOnlyList<LineOaDto>>
{
    public async Task<IReadOnlyList<LineOaDto>> HandleAsync(GetLineOasQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        await users.RequireLineChatAsync(ws, ct);
        return await LineOaRows.BuildAsync(ws.Id, accounts, devices, lineChats, sets, clock.GetUtcNow(), ct);
    }
}

/// <summary>Adds an OA by its address and asks the chosen browser to read its customers right away.</summary>
public sealed record AddLineOaCommand(Guid WorkspaceId, string Url, Guid? DeviceId, string? Name) : ICommand<LineOaDto>;

public sealed class AddLineOaCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts, IDeviceRepository devices,
    ILineChatRepository lineChats, ILinkSetRepository sets, IExtensionRepository ext, IDeviceEventRepository events,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<AddLineOaCommand, LineOaDto>
{
    public async Task<LineOaDto> HandleAsync(AddLineOaCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        await users.RequireLineChatAsync(ws, ct);
        var botId = LineOaUrl.BotId(c.Url)
            ?? throw new DomainException("ลิงก์ LINE OA ไม่ถูกต้อง: วางลิงก์จาก chat.line.biz เช่น https://chat.line.biz/Uxxxxxxxx");

        var list = await devices.ListAsync(ws.Id, ct);
        Device device;
        if (c.DeviceId is { } did)
            device = list.FirstOrDefault(d => d.Id == did) ?? throw new NotFoundException("อุปกรณ์", did);
        else if (list.Count == 1)
            device = list[0];
        else
            throw new DomainException(list.Count == 0
                ? "ยังไม่มีส่วนขยายที่เชื่อมไว้: เชื่อมส่วนขยายก่อน แล้วค่อยเพิ่ม LINE OA"
                : "มีส่วนขยายหลายเครื่อง: เลือกเครื่องที่จะอ่านรายชื่อลูกค้าของ LINE OA นี้");

        var now = clock.GetUtcNow();
        if (await accounts.FindByHandleAsync(ws.Id, Platform.Line, botId, ct) is not null)
            throw new DomainException("LINE OA นี้อยู่ในรายการแล้ว");
        var name = string.IsNullOrWhiteSpace(c.Name) ? botId : c.Name.Trim();
        var account = SocialAccount.CreateLine(ws.Id, device.Id, botId, name, now, await accounts.CountAsync(ws.Id, ct));
        accounts.Add(account);
        LineOaCommands.Ask(ext, events, ws.Id, device.Id, botId, now);
        await uow.SaveChangesAsync(ct);
        return (await LineOaRows.BuildAsync(ws.Id, accounts, devices, lineChats, sets, now, ct)).First(r => r.Id == account.Id);
    }
}

/// <summary>Asks the browser of an OA to read its customers again.</summary>
public sealed record SyncLineOaCommand(Guid WorkspaceId, Guid AccountId) : ICommand<bool>;

public sealed class SyncLineOaCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts, IDeviceRepository devices, IExtensionRepository ext,
    IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SyncLineOaCommand, bool>
{
    public async Task<bool> HandleAsync(SyncLineOaCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        await users.RequireLineChatAsync(ws, ct);
        var a = await accounts.GetAsync(ws.Id, c.AccountId, ct);
        if (a is null || a.Platform != Platform.Line) throw new NotFoundException("LINE OA", c.AccountId);
        var deviceId = await LineOaCommands.DeviceOfAsync(a, devices, ws.Id, ct);
        LineOaCommands.Ask(ext, events, ws.Id, deviceId, a.Handle, clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Tells the browser of an OA to work for that OA from now on (the web chooses; the extension's own page no longer has to).</summary>
public sealed record UseLineOaCommand(Guid WorkspaceId, Guid AccountId) : ICommand<bool>;

public sealed class UseLineOaCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts, IDeviceRepository devices, IExtensionRepository ext,
    IDeviceEventRepository events, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UseLineOaCommand, bool>
{
    public async Task<bool> HandleAsync(UseLineOaCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        await users.RequireLineChatAsync(ws, ct);
        var a = await accounts.GetAsync(ws.Id, c.AccountId, ct);
        if (a is null || a.Platform != Platform.Line) throw new NotFoundException("LINE OA", c.AccountId);
        var deviceId = await LineOaCommands.DeviceOfAsync(a, devices, ws.Id, ct);
        LineOaCommands.Ask(ext, events, ws.Id, deviceId, a.Handle, clock.GetUtcNow(), "lineUseOa");
        await uow.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Takes an OA out of the master list: its customers and posts go with it, and the link sets that used it wait for another OA.</summary>
public sealed record RemoveLineOaCommand(Guid WorkspaceId, Guid AccountId) : ICommand<bool>;

public sealed class RemoveLineOaCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAccountRepository accounts, ILineChatRepository lineChats,
    ILinkSetRepository sets, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<RemoveLineOaCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveLineOaCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireLineChatAsync(ws, ct);
        var a = await accounts.GetAsync(ws.Id, c.AccountId, ct);
        if (a is null || a.Platform != Platform.Line) throw new NotFoundException("LINE OA", c.AccountId);
        // The shop asked for this OA to go (the web says what goes with it): the link sets that posted through it are left with no OA
        // (they wait until one is chosen again) and its posts, queued or done, go with the account (the database cascades them).
        foreach (var set in (await sets.ListAsync(ws.Id, ct)).Where(x => x.PostAsAccountId == a.Id))
            set.Update(set.Name, null, null);
        await lineChats.DeleteByAccountAsync(a.Id, ct);
        accounts.Remove(a);
        await uow.SaveChangesAsync(ct);
        return true;
    }
}
