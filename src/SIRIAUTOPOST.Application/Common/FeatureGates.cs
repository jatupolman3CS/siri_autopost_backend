using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// Plan-gated features of a workspace. The plan that counts is the workspace OWNER's, whoever is asking
/// (the same rule as the limits and the advanced anti-ban settings).
/// </summary>
public static class FeatureGates
{
    public const string ProRequired = "ต้องใช้แผน Pro ขึ้นไป";
    public const string AgencyRequired = "ต้องใช้แผน Agency";
    public const string PremiumRequired = "การดันโพสต์ต้องใช้แผน Premium (Agency)";
    public const string LineChatRequired = "แชท LINE ใช้ได้ในแพ็กเกจ Pro ขึ้นไป";

    public static async Task RequireLineChatAsync(this IUserRepository users, Workspace ws, CancellationToken ct)
    {
        if (!(await users.OwnerOfAsync(ws, ct)).HasLineChat) throw new ForbiddenException(LineChatRequired);
    }

    public static async Task RequireNotificationsAsync(this IUserRepository users, Workspace ws, CancellationToken ct)
    {
        if (!(await users.OwnerOfAsync(ws, ct)).HasNotifications) throw new ForbiddenException(ProRequired);
    }

    public static async Task RequireAutoReplyAsync(this IUserRepository users, Workspace ws, CancellationToken ct)
    {
        if (!(await users.OwnerOfAsync(ws, ct)).HasAutoReply) throw new ForbiddenException(ProRequired);
    }

    public static async Task RequireClientReportsAsync(this IUserRepository users, Workspace ws, CancellationToken ct)
    {
        if (!(await users.OwnerOfAsync(ws, ct)).HasClientReports) throw new ForbiddenException(AgencyRequired);
    }
}
