using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Team;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>People with access to a workspace. Reading needs any role; changes need admin (or leaving yourself).</summary>
[ApiController]
[Route("api/workspaces/{wsId:guid}/members")]
[Produces("application/json")]
public sealed class TeamController : ControllerBase
{
    public sealed record InviteRequest(string Email, WorkspaceRole Role);
    public sealed record RoleRequest(WorkspaceRole Role);

    [HttpGet]
    public Task<IReadOnlyList<MemberDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetMembersQuery, IReadOnlyList<MemberDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetMembersQuery(wsId), ct);

    /// <summary>Invites by email: an existing user joins at once, a new one when they sign up with that email.</summary>
    [HttpPost]
    public Task<MemberDto> Invite(
        Guid wsId, InviteRequest r, [FromServices] ICommandHandler<InviteMemberCommand, MemberDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new InviteMemberCommand(wsId, r.Email, r.Role), ct);

    [HttpPut("{memberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeRole(
        Guid wsId, Guid memberId, RoleRequest r, [FromServices] ICommandHandler<ChangeMemberRoleCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new ChangeMemberRoleCommand(wsId, memberId, r.Role), ct);
        return NoContent();
    }

    [HttpDelete("{memberId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(
        Guid wsId, Guid memberId, [FromServices] ICommandHandler<RemoveMemberCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new RemoveMemberCommand(wsId, memberId), ct);
        return NoContent();
    }
}
