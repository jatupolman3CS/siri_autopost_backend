using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

/// <summary>Per-customer limits set by the platform admin; null keeps the plan's value, 0 means unlimited.</summary>
public class LimitOverrides
{
    public int? Accounts { get; set; }
    public int? Posts { get; set; }
    public int? Devices { get; set; }
    public int? Seats { get; set; }

    public bool IsEmpty => Accounts is null && Posts is null && Devices is null && Seats is null;
}

/// <summary>What a customer may use; null = unlimited.</summary>
public sealed record EffectiveLimits(int? Accounts, int? Posts, int? Devices, int? Seats)
{
    public static EffectiveLimits Of(Entities.PlanSetting plan, LimitOverrides? o)
    {
        static int? Pick(int? over, int? planValue) => over is null ? planValue : over == 0 ? null : over;
        return new(Pick(o?.Accounts, plan.Accounts), Pick(o?.Posts, plan.Posts), Pick(o?.Devices, plan.Devices), Pick(o?.Seats, plan.Seats));
    }
}

/// <summary>Billing arithmetic shared by self-service plan changes and the admin's refunds.</summary>
public static class Pricing
{
    /// <summary>Yearly billing costs 80% of the monthly price, paid for 12 months up front.</summary>
    public const decimal YearlyFactor = 0.8m;

    public static int PerMonth(int price, BillingCycle cycle) =>
        cycle == BillingCycle.Year ? (int)Math.Round(price * YearlyFactor, MidpointRounding.AwayFromZero) : price;

    /// <summary>The charge for one billing period, after a promo code's discount.</summary>
    public static int Charge(int price, BillingCycle cycle, string? discount)
    {
        var month = PerMonth(price, cycle);
        var months = cycle == BillingCycle.Year ? 12 : 1;
        decimal total = month * months;
        total = discount switch
        {
            "d10" => total * 0.9m,
            "d20" => total * 0.8m,
            "d30" => total * 0.7m,
            "dFree" => total - month, // the first month is free
            null or "" => total,
            _ => throw new DomainException("ส่วนลดไม่ถูกต้อง"),
        };
        return (int)Math.Round(total, MidpointRounding.AwayFromZero);
    }
}
