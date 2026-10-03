using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using Stripe;

namespace SIRIAUTOPOST.Infrastructure.Payments;

/// <summary>Stripe objects to the neutral snapshots the Application works with.</summary>
internal static class StripeMapping
{
    /// <summary>Our keys in Stripe metadata (subscription, session, customer, coupon).</summary>
    public const string UserKey = "user_id", PlanKey = "plan", CycleKey = "cycle", PromoKey = "promo";

    public static DateTimeOffset At(DateTime stripeTime) => new(DateTime.SpecifyKind(stripeTime, DateTimeKind.Utc));

    public static string Key(Domain.Enums.PlanKey plan) => AuditEntry.Key(plan);

    public static string Key(BillingCycle cycle) => cycle == BillingCycle.Year ? "year" : "month";

    public static string Interval(BillingCycle cycle) => Key(cycle);

    public static Dictionary<string, string> Metadata(Guid userId, Domain.Enums.PlanKey plan, BillingCycle cycle, string? promo = null)
    {
        var m = new Dictionary<string, string> { [UserKey] = userId.ToString(), [PlanKey] = Key(plan), [CycleKey] = Key(cycle) };
        if (!string.IsNullOrEmpty(promo)) m[PromoKey] = promo;
        return m;
    }

    private static (Domain.Enums.PlanKey? Plan, BillingCycle? Cycle) PlanOf(IDictionary<string, string>? metadata, string? interval = null)
    {
        Domain.Enums.PlanKey? plan = null;
        BillingCycle? cycle = interval switch { "year" => BillingCycle.Year, "month" => BillingCycle.Month, _ => null };
        if (metadata is not null)
        {
            if (metadata.TryGetValue(PlanKey, out var p) && Enum.TryParse<Domain.Enums.PlanKey>(p, true, out var pk) && pk != Domain.Enums.PlanKey.Free) plan = pk;
            if (metadata.TryGetValue(CycleKey, out var c) && Enum.TryParse<BillingCycle>(c, true, out var ck)) cycle = ck;
        }
        return (plan, cycle);
    }

    public static SubscriptionState State(string? status) => status switch
    {
        "active" or "trialing" or "paused" => SubscriptionState.Active,
        "past_due" => SubscriptionState.PastDue,
        "canceled" or "unpaid" or "incomplete_expired" => SubscriptionState.Canceled,
        _ => SubscriptionState.Incomplete,
    };

    public static SubscriptionSnapshot Subscription(Subscription s)
    {
        var item = s.Items?.Data?.FirstOrDefault();
        var (plan, cycle) = PlanOf(s.Metadata, item?.Price?.Recurring?.Interval);
        // A scheduled cancellation is either the flag or a cancel_at date (the portal may set either).
        var cancelAt = s.CancelAt is { } a ? At(a) : (DateTimeOffset?)null;
        var renews = cancelAt ?? (item is null ? null : At(item.CurrentPeriodEnd));
        return new SubscriptionSnapshot(s.Id, s.CustomerId, State(s.Status), plan, cycle, renews, s.CancelAtPeriodEnd || cancelAt is not null);
    }

    public static CheckoutSessionSnapshot Session(Stripe.Checkout.Session s)
    {
        var paid = s.Status == "complete" && s.PaymentStatus is "paid" or "no_payment_required";
        var user = Guid.TryParse(s.ClientReferenceId, out var id) ? id : (Guid?)null;
        var promo = s.Metadata is not null && s.Metadata.TryGetValue(PromoKey, out var code) ? code : null;
        return new CheckoutSessionSnapshot(s.Id, s.CustomerId, s.SubscriptionId, paid, user, promo);
    }

    /// <param name="paid">The amount is what was collected (AmountPaid) rather than what was asked for (AmountDue).</param>
    public static InvoiceSnapshot Invoice(Invoice i, bool paid)
    {
        var details = i.Parent?.SubscriptionDetails;
        var (plan, cycle) = PlanOf(details?.Metadata);
        var first = i.BillingReason == "subscription_create";
        var promo = first && details?.Metadata is { } m && m.TryGetValue(PromoKey, out var code) ? code : null;
        return new InvoiceSnapshot(
            i.Id, i.CustomerId, details?.SubscriptionId, Money.FromSatang(paid ? i.AmountPaid : i.AmountDue), At(i.Created),
            i.HostedInvoiceUrl, first, plan, cycle, promo);
    }

    public static RefundSnapshot Refund(Refund r) =>
        new(r.Id, r.PaymentIntentId, Money.FromSatang(r.Amount), r.Status is "succeeded" or "pending", At(r.Created));
}
