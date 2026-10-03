using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

public static class WorkspaceAccess
{
    /// <summary>The workspace, if the signed-in user owns it. Someone else's workspace reads as not found.</summary>
    public static async Task<Workspace> RequireOwnedAsync(
        this IWorkspaceRepository workspaces, Guid workspaceId, ICurrentUser user, CancellationToken ct)
    {
        var ws = await workspaces.GetByIdAsync(workspaceId, ct);
        if (ws is null || ws.OwnerId != user.UserId) throw new NotFoundException("เวิร์กสเปซ", workspaceId);
        return ws;
    }
}
