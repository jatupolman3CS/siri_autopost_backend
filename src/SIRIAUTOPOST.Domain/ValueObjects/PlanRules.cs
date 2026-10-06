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
    public int? Groups { get; set; }
    public int? Images { get; set; }
    public int? LibraryPosts { get; set; }

    public bool IsEmpty =>
        Accounts is null && Posts is null && Devices is null && Seats is null && Groups is null && Images is null && LibraryPosts is null;
}

/// <summary>What a customer may use; null = unlimited.</summary>
public sealed record EffectiveLimits(
    int? Accounts, int? Posts, int? Devices, int? Seats, int? Groups = null, int? Images = null, int? LibraryPosts = null)
{
    public static EffectiveLimits Of(Entities.PlanSetting plan, LimitOverrides? o)
    {
        static int? Pick(int? over, int? planValue) => over is null ? planValue : over == 0 ? null : over;
        return new(
            Pick(o?.Accounts, plan.Accounts), Pick(o?.Posts, plan.Posts), Pick(o?.Devices, plan.Devices), Pick(o?.Seats, plan.Seats),
            Pick(o?.Groups, plan.Groups), Pick(o?.Images, plan.Images), Pick(o?.LibraryPosts, plan.LibraryPosts));
    }
}

/// <summary>Billing arithmetic shared by self-service plan changes and the admin's refunds.</summary>
public static class Pricing
{
    /// <summary>Yearly billing costs 80% of the monthly price, paid for 12 months up front.</summary>
    public const decimal YearlyFactor = 0.8m;

    public static int PerMonth(int price, BillingCycle cycle) =>
        cycle == BillingCycle.Year ? (int)Math.Round(price * YearlyFactor, MidpointRounding.AwayFromZero) : price;

    /// <summary>The price of one billing period before any discount (a year is 12 months at the yearly rate).</summary>
    public static int Period(int price, BillingCycle cycle) => PerMonth(price, cycle) * (cycle == BillingCycle.Year ? 12 : 1);

    /// <summary>What a promo code takes off the first invoice; Stripe gets this as an amount-off coupon.</summary>
    public static int FirstDiscount(int price, BillingCycle cycle, string? discount) => Period(price, cycle) - Charge(price, cycle, discount);

    /// <summary>The charge for one billing period, after a promo code's discount.</summary>
    public static int Charge(int price, BillingCycle cycle, string? discount)
    {
        var month = PerMonth(price, cycle);
        decimal total = Period(price, cycle);
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
