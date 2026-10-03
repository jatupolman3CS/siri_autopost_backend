using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Team;

/// <summary>Everyone with access to the workspace: the owner first, then members and pending invitations.</summary>
public sealed record GetMembersQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<MemberDto>>;

public sealed class GetMembersQueryHandler(
    IWorkspaceRepository workspaces, IMemberRepository members, IUserRepository users, ICurrentUser current)
    : IQueryHandler<GetMembersQuery, IReadOnlyList<MemberDto>>
{
    public async Task<IReadOnlyList<MemberDto>> HandleAsync(GetMembersQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var list = await members.ListAsync(ws.Id, ct);
        var people = (await users.ListByIdsAsync(list.Where(m => m.UserId is not null).Select(m => m.UserId!.Value).Append(ws.OwnerId), ct))
            .ToDictionary(u => u.Id);
        var owner = people[ws.OwnerId];
        var rows = new List<MemberDto>
        {
            new(null, owner.Id, owner.Email, owner.Name, WorkspaceRole.Owner, true, owner.LastSeenAt, owner.Id == current.UserId),
        };
        rows.AddRange(list.Select(m =>
        {
            var u = m.UserId is { } id && people.TryGetValue(id, out var p) ? p : null;
            return new MemberDto(m.Id, m.UserId, m.Email, u?.Name ?? m.Email.Split('@')[0], m.Role, m.IsActive, u?.LastSeenAt,
                m.UserId == current.UserId);
        }));
        return rows;
    }
}

/// <summary>Invites by email. Seats (owner included) come from the owner's plan.</summary>
public sealed record InviteMemberCommand(Guid WorkspaceId, string Email, WorkspaceRole Role) : ICommand<MemberDto>;

public sealed class InviteMemberCommandHandler(
    IWorkspaceRepository workspaces, IMemberRepository members, IUserRepository users, IPlanRepository plans,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<InviteMemberCommand, MemberDto>
{
    public async Task<MemberDto> HandleAsync(InviteMemberCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var owner = await users.OwnerOfAsync(ws, ct);
        var email = User.NormalizeEmail(c.Email);
        if (email == owner.Email) throw new ConflictException("อีเมลนี้เป็นเจ้าของเวิร์กสเปซอยู่แล้ว");
        var list = await members.ListAsync(ws.Id, ct);
        if (list.Any(m => m.Email == email)) throw new ConflictException("อีเมลนี้อยู่ในทีมแล้ว");
        var seats = (await plans.ForAsync(owner, ct)).Seats;
        if (seats is { } max && list.Count + 1 >= max)
            throw new DomainException(max <= 1
                ? "แผนปัจจุบันยังเชิญสมาชิกไม่ได้ อัปเกรดเป็นแผน Agency เพื่อทำงานเป็นทีม"
                : $"ทีมเต็มแล้ว แผนปัจจุบันมีได้สูงสุด {max} คน (รวมเจ้าของ)");

        var existing = await users.GetByEmailAsync(email, ct);
        var member = WorkspaceMember.Invite(ws.Id, email, c.Role, existing, clock.GetUtcNow());
        members.Add(member);
        await uow.SaveChangesAsync(ct);
        return new MemberDto(member.Id, member.UserId, member.Email, existing?.Name ?? email.Split('@')[0], member.Role,
            member.IsActive, existing?.LastSeenAt, false);
    }
}

public sealed record ChangeMemberRoleCommand(Guid WorkspaceId, Guid MemberId, WorkspaceRole Role) : ICommand<Unit>;

public sealed class ChangeMemberRoleCommandHandler(
    IWorkspaceRepository workspaces, IMemberRepository members, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<ChangeMemberRoleCommand, Unit>
{
    public async Task<Unit> HandleAsync(ChangeMemberRoleCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var m = await members.GetAsync(ws.Id, c.MemberId, ct) ?? throw new NotFoundException("สมาชิก", c.MemberId);
        if (m.UserId == current.UserId) throw new DomainException("เปลี่ยนบทบาทของตัวเองไม่ได้");
        m.ChangeRole(c.Role);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Removes a member or cancels an invitation. Members may also leave on their own.</summary>
public sealed record RemoveMemberCommand(Guid WorkspaceId, Guid MemberId) : ICommand<Unit>;

public sealed class RemoveMemberCommandHandler(
    IWorkspaceRepository workspaces, IMemberRepository members, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<RemoveMemberCommand, Unit>
{
    public async Task<Unit> HandleAsync(RemoveMemberCommand c, CancellationToken ct = default)
    {
        var (ws, role) = await workspaces.RequireRoleAsync(c.WorkspaceId, current, ct);
        var m = await members.GetAsync(ws.Id, c.MemberId, ct) ?? throw new NotFoundException("สมาชิก", c.MemberId);
        if (m.UserId != current.UserId && role < WorkspaceRole.Admin)
            throw new ForbiddenException("สิทธิ์ของคุณในเวิร์กสเปซนี้ทำรายการนี้ไม่ได้");
        members.Remove(m);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
