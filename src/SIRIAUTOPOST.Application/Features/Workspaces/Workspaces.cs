using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Workspaces;

public sealed record GetWorkspacesQuery : IQuery<IReadOnlyList<WorkspaceDto>>;

/// <summary>The user's own workspaces, then the ones they were invited into.</summary>
public sealed class GetWorkspacesQueryHandler(
    IWorkspaceRepository workspaces, IMemberRepository members, IPostRepository posts, IUserRepository users, IPlanRepository plans,
    ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetWorkspacesQuery, IReadOnlyList<WorkspaceDto>>
{
    public async Task<IReadOnlyList<WorkspaceDto>> HandleAsync(GetWorkspacesQuery q, CancellationToken ct = default)
    {
        var own = await workspaces.ListByOwnerAsync(current.UserId, ct);
        var memberships = (await members.ListByUserAsync(current.UserId, ct)).ToDictionary(m => m.WorkspaceId, m => m.Role);
        var joined = await workspaces.ListByIdsAsync(memberships.Keys, ct);
        var all = own.Concat(joined.Where(w => w.OwnerId != current.UserId)).ToList();
        var ids = all.Select(w => w.Id).ToList();
        var sent = await posts.CountSentSinceAsync(ids, clock.GetUtcNow().AddDays(-7), ct);
        var people = new Dictionary<Guid, int>();
        foreach (var id in ids) people[id] = 1 + (await members.ListAsync(id, ct)).Count(m => m.IsActive);
        // Limits and plan features belong to the owner, not to whoever is looking.
        var owners = (await users.ListByIdsAsync(all.Select(w => w.OwnerId), ct)).ToDictionary(u => u.Id);
        var limits = new Dictionary<Guid, LimitsDto>();
        foreach (var o in owners.Values) limits[o.Id] = LimitsDto.From(await plans.ForAsync(o, ct));
        return all.Select(w => new WorkspaceDto(
                w.Id, w.Name, sent.GetValueOrDefault(w.Id), people[w.Id],
                w.OwnerId == current.UserId ? WorkspaceRole.Owner : memberships[w.Id],
                limits[w.OwnerId], owners[w.OwnerId].HasAdvancedAntiBan))
            .ToList();
    }
}

public sealed record CreateWorkspaceCommand(string Name) : ICommand<WorkspaceDto>;

public sealed class CreateWorkspaceCommandHandler(
    IWorkspaceRepository workspaces, IWorkspaceSeeder seeder, IUserRepository users, IPlanRepository plans, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateWorkspaceCommand, WorkspaceDto>
{
    public async Task<WorkspaceDto> HandleAsync(CreateWorkspaceCommand c, CancellationToken ct = default)
    {
        var ws = Workspace.Create(current.UserId, c.Name, clock.GetUtcNow());
        workspaces.Add(ws);
        await seeder.SeedAsync(ws, ct);
        await uow.SaveChangesAsync(ct);
        var owner = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        return new WorkspaceDto(ws.Id, ws.Name, 0, 1, WorkspaceRole.Owner, LimitsDto.From(await plans.ForAsync(owner, ct)), owner.HasAdvancedAntiBan);
    }
}
