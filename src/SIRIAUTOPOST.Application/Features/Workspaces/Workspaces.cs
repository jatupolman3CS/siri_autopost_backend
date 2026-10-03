using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Workspaces;

public sealed record GetWorkspacesQuery : IQuery<IReadOnlyList<WorkspaceDto>>;

public sealed class GetWorkspacesQueryHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetWorkspacesQuery, IReadOnlyList<WorkspaceDto>>
{
    public async Task<IReadOnlyList<WorkspaceDto>> HandleAsync(GetWorkspacesQuery q, CancellationToken ct = default)
    {
        var list = await workspaces.ListByOwnerAsync(current.UserId, ct);
        var sent = await posts.CountSentSinceAsync(list.Select(w => w.Id), clock.GetUtcNow().AddDays(-7), ct);
        // Team members are not modelled yet: the owner is the only member.
        return list.Select(w => new WorkspaceDto(w.Id, w.Name, sent.GetValueOrDefault(w.Id), 1)).ToList();
    }
}

public sealed record CreateWorkspaceCommand(string Name) : ICommand<WorkspaceDto>;

public sealed class CreateWorkspaceCommandHandler(
    IWorkspaceRepository workspaces, IWorkspaceSeeder seeder, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateWorkspaceCommand, WorkspaceDto>
{
    public async Task<WorkspaceDto> HandleAsync(CreateWorkspaceCommand c, CancellationToken ct = default)
    {
        var ws = Workspace.Create(current.UserId, c.Name, clock.GetUtcNow());
        workspaces.Add(ws);
        await seeder.SeedAsync(ws, ct);
        await uow.SaveChangesAsync(ct);
        return new WorkspaceDto(ws.Id, ws.Name, 0, 1);
    }
}
