using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Engine;

public sealed record GetEngineSettingsQuery(Guid WorkspaceId) : IQuery<EngineSettingsDto>;

public sealed class GetEngineSettingsQueryHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetEngineSettingsQuery, EngineSettingsDto>
{
    public async Task<EngineSettingsDto> HandleAsync(GetEngineSettingsQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return EngineSettingsDto.From(ws, await devices.ListAsync(ws.Id, ct), clock.GetUtcNow());
    }
}

public sealed record UpdateAntiBanCommand(Guid WorkspaceId, AntiBanDto Settings) : ICommand<EngineSettingsDto>;

/// <summary>Human-like behaviour switches only change on Pro and above; lower plans keep their current values.</summary>
public sealed class UpdateAntiBanCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IDeviceRepository devices, ICurrentUser current, IUnitOfWork uow,
    TimeProvider clock)
    : ICommandHandler<UpdateAntiBanCommand, EngineSettingsDto>
{
    public async Task<EngineSettingsDto> HandleAsync(UpdateAntiBanCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        // The workspace owner's plan decides, whoever edits.
        var owner = await users.GetByIdAsync(ws.OwnerId, ct) ?? throw new NotFoundException("ผู้ใช้", ws.OwnerId);
        ws.UpdateAntiBan(c.Settings.ToSettings(), owner.HasAdvancedAntiBan);
        await uow.SaveChangesAsync(ct);
        return EngineSettingsDto.From(ws, await devices.ListAsync(ws.Id, ct), clock.GetUtcNow());
    }
}

public sealed record UpdateOfflineCommand(Guid WorkspaceId, OfflineDto Settings) : ICommand<EngineSettingsDto>;

public sealed class UpdateOfflineCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateOfflineCommand, EngineSettingsDto>
{
    public async Task<EngineSettingsDto> HandleAsync(UpdateOfflineCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        ws.UpdateOffline(c.Settings.ToSettings());
        await uow.SaveChangesAsync(ct);
        return EngineSettingsDto.From(ws, await devices.ListAsync(ws.Id, ct), clock.GetUtcNow());
    }
}

/// <summary>
/// "Simulate offline / reconnect" from the design. Going offline holds the next 4 queued posts due in the
/// next 12 hours, and paired devices take no jobs; reconnecting puts them back in the queue (a device posts
/// them next), or skips them under the skip policy.
/// </summary>
public sealed record SetExtensionOnlineCommand(Guid WorkspaceId, bool Online) : ICommand<ExtensionStateDto>;

public sealed class SetExtensionOnlineCommandHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetExtensionOnlineCommand, ExtensionStateDto>
{
    public const int HeldPosts = 4;

    public async Task<ExtensionStateDto> HandleAsync(SetExtensionOnlineCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var now = clock.GetUtcNow();
        var affected = 0;
        if (!c.Online && ws.ExtensionOnline)
        {
            var due = (await posts.ListByStatusAsync(ws.Id, PostStatus.Queued, ct))
                .Where(p => p.ScheduledAt > now && p.ScheduledAt <= now.AddHours(12))
                .Take(HeldPosts);
            foreach (var p in due)
            {
                p.MarkWaiting(now);
                affected++;
            }
        }
        else if (c.Online && !ws.ExtensionOnline)
        {
            var skip = ws.Offline.Policy == OfflinePolicy.Skip;
            foreach (var p in await posts.ListByStatusAsync(ws.Id, PostStatus.Waiting, ct))
            {
                p.ResolveWaiting(skip, now);
                affected++;
            }
        }
        ws.SetExtensionOnline(c.Online);
        await uow.SaveChangesAsync(ct);
        return new ExtensionStateDto(c.Online, affected);
    }
}

/// <summary>Offline banner "Skip these posts".</summary>
public sealed record SkipWaitingPostsCommand(Guid WorkspaceId) : ICommand<ExtensionStateDto>;

public sealed class SkipWaitingPostsCommandHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SkipWaitingPostsCommand, ExtensionStateDto>
{
    public async Task<ExtensionStateDto> HandleAsync(SkipWaitingPostsCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var now = clock.GetUtcNow();
        var waiting = await posts.ListByStatusAsync(ws.Id, PostStatus.Waiting, ct);
        foreach (var p in waiting) p.ResolveWaiting(skip: true, now);
        await uow.SaveChangesAsync(ct);
        return new ExtensionStateDto(ws.ExtensionOnline, waiting.Count);
    }
}
