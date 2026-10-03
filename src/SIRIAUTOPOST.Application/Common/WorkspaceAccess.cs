using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

public static class WorkspaceAccess
{
    /// <summary>
    /// The workspace, if the signed-in user owns it or is a member with at least <paramref name="min"/>.
    /// A workspace they have no part in reads as not found; too low a role is 403.
    /// Viewer reads; Editor writes posts and the library; Admin changes settings, devices and members.
    /// </summary>
    public static async Task<Workspace> RequireAsync(
        this IWorkspaceRepository workspaces, Guid workspaceId, ICurrentUser user, WorkspaceRole min, CancellationToken ct)
    {
        var (ws, role) = await workspaces.RequireRoleAsync(workspaceId, user, ct);
        if (role < min) throw new ForbiddenException("สิทธิ์ของคุณในเวิร์กสเปซนี้ทำรายการนี้ไม่ได้");
        return ws;
    }

    public static async Task<(Workspace Workspace, WorkspaceRole Role)> RequireRoleAsync(
        this IWorkspaceRepository workspaces, Guid workspaceId, ICurrentUser user, CancellationToken ct)
    {
        var ws = await workspaces.GetByIdAsync(workspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", workspaceId);
        var role = ws.OwnerId == user.UserId ? WorkspaceRole.Owner : await workspaces.GetMemberRoleAsync(ws.Id, user.UserId, ct);
        return role is { } r ? (ws, r) : throw new NotFoundException("เวิร์กสเปซ", workspaceId);
    }
}
