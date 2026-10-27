using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.LineChats;

public sealed record GetLineChatsQuery(
    Guid WorkspaceId,
    Guid? AccountId = null,
    string? Q = null,
    string? Label = null,
    int? ActiveWithinDays = null,
    bool? DoNotSend = null,
    int Page = 1,
    int PageSize = 20) : IQuery<LineChatPageDto>;

public sealed class GetLineChatsQueryHandler(
    IWorkspaceRepository workspaces,
    IUserRepository users,
    ILineChatRepository lineChats,
    IAccountRepository accounts,
    IDeviceRepository devices,
    ICurrentUser current,
    TimeProvider clock)
    : IQueryHandler<GetLineChatsQuery, LineChatPageDto>
{
    public async Task<LineChatPageDto> HandleAsync(GetLineChatsQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        await users.RequireLineChatAsync(ws, ct);

        var filter = new LineChatFilter(
            q.WorkspaceId,
            q.AccountId,
            q.Q,
            q.Label,
            q.ActiveWithinDays,
            q.DoNotSend,
            q.Page,
            q.PageSize);

        var (items, total, labels) = await lineChats.PageAsync(filter, ct);
        var linkedCounts = await lineChats.LinkedSetCountsAsync(items.Select(x => x.Id), ct);

        var allAccounts = await accounts.ListAsync(q.WorkspaceId, ct);
        var lineAccounts = allAccounts.Where(a => a.Platform == Platform.Line).ToList();
        var allDevices = await devices.ListAsync(q.WorkspaceId, ct);
        var deviceById = allDevices.ToDictionary(d => d.Id);
        var now = clock.GetUtcNow();

        var accountSummaries = lineAccounts.Select(a =>
        {
            var isReady = a.DeviceId.HasValue && deviceById.TryGetValue(a.DeviceId.Value, out var dev) && dev.IsLineReady(now);
            return new LineChatAccountSummaryDto(a.Id, a.Name, a.DeviceId, isReady);
        }).ToList();

        var dtos = items.Select(x => new LineChatDto(
            x.Id,
            x.AccountId,
            x.Name,
            x.ChatUrl,
            x.Labels,
            x.LastMessageAt,
            x.Unread,
            x.DoNotSend,
            linkedCounts.GetValueOrDefault(x.Id))).ToList();

        return new LineChatPageDto(dtos, total, labels, accountSummaries);
    }
}

public sealed record UpdateLineChatCommand(Guid WorkspaceId, Guid ChatId, bool DoNotSend) : ICommand<LineChatDto>;

public sealed class UpdateLineChatCommandHandler(
    IWorkspaceRepository workspaces,
    IUserRepository users,
    ILineChatRepository lineChats,
    ICurrentUser current,
    IUnitOfWork uow)
    : ICommandHandler<UpdateLineChatCommand, LineChatDto>
{
    public async Task<LineChatDto> HandleAsync(UpdateLineChatCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        await users.RequireLineChatAsync(ws, ct);

        var chat = await lineChats.GetAsync(c.WorkspaceId, c.ChatId, ct)
            ?? throw new NotFoundException("แชท LINE", c.ChatId);

        chat.SetDoNotSend(c.DoNotSend);
        await uow.SaveChangesAsync(ct);

        var linkedCounts = await lineChats.LinkedSetCountsAsync([chat.Id], ct);

        return new LineChatDto(
            chat.Id,
            chat.AccountId,
            chat.Name,
            chat.ChatUrl,
            chat.Labels,
            chat.LastMessageAt,
            chat.Unread,
            chat.DoNotSend,
            linkedCounts.GetValueOrDefault(chat.Id));
    }
}
