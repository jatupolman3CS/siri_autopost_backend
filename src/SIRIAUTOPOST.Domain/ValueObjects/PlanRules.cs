using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.ValueObjects;

/// <summary>Limits that depend on the owner's plan (same numbers as the pricing page).</summary>
public static class PlanRules
{
    /// <summary>Devices per workspace; null = unlimited.</summary>
    public static int? MaxDevices(PlanKey plan) => plan switch
    {
        PlanKey.Free or PlanKey.Basic => 1,
        PlanKey.Pro => 3,
        _ => null,
    };
}
