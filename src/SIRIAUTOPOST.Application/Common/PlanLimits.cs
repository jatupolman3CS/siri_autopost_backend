using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>A workspace owner's limits: their plan's, with the platform admin's overrides.</summary>
public static class PlanLimits
{
    public static async Task<EffectiveLimits> ForAsync(this IPlanRepository plans, User owner, CancellationToken ct) =>
        EffectiveLimits.Of(await plans.GetAsync(owner.Plan, ct), owner.Limits);

    public static async Task<User> OwnerOfAsync(this IUserRepository users, Workspace ws, CancellationToken ct) =>
        await users.GetByIdAsync(ws.OwnerId, ct) ?? throw new NotFoundException("ผู้ใช้", ws.OwnerId);
}
