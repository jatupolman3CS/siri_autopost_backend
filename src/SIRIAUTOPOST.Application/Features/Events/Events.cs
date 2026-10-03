using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Events;

// The workspace's event stream: what its devices did, in order (DeviceEvent). The API streams new events
// live; this query is how a client catches up after a lost connection (everything after the last Seq it
// saw) and how it learns the head Seq to resume from.

/// <param name="AfterSeq">Events after this Seq; 0 for the oldest kept.</param>
/// <param name="Take">At most this many (clamped to <see cref="GetWorkspaceEventsQueryHandler.MaxTake"/>).</param>
public sealed record GetWorkspaceEventsQuery(Guid WorkspaceId, long AfterSeq, int Take) : IQuery<DeviceEventsPageDto>;

public sealed class GetWorkspaceEventsQueryHandler(IWorkspaceRepository workspaces, IDeviceEventRepository events, ICurrentUser current)
    : IQueryHandler<GetWorkspaceEventsQuery, DeviceEventsPageDto>
{
    public const int MaxTake = 500;

    public async Task<DeviceEventsPageDto> HandleAsync(GetWorkspaceEventsQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var take = Math.Clamp(q.Take, 1, MaxTake);
        var list = await events.ListAfterAsync(ws.Id, Math.Max(0, q.AfterSeq), take + 1, ct);
        var more = list.Count > take;
        var page = (more ? list.Take(take) : list).Select(DeviceEventDto.From).ToList();
        var head = await events.HeadAsync(ws.Id, ct);
        return new DeviceEventsPageDto(head, page, more);
    }
}
