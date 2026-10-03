using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.ValueObjects;

// The admin overview's subscription figures, rebuilt from the plan-change entries of the activity
// log: the plan a customer had at an earlier time is the "from" of their first change after it.
// Billing cycles and statuses are not kept in history, so the current ones are used.
public static class PlatformMetrics
{
    /// <summary>The customer's plan at a time, or null when they had not signed up yet.</summary>
    public static PlanKey? PlanAt(User user, IEnumerable<AuditEntry> changes, DateTimeOffset at)
    {
        if (user.CreatedAt > at) return null;
        var next = changes
            .Where(e => e.CustomerId == user.Id && e.Action == AuditAction.PlanChanged && e.At > at)
            .MinBy(e => e.At);
        return next?.From is { } from && Enum.TryParse<PlanKey>(from, true, out var plan) ? plan : user.Plan;
    }

    /// <summary>Monthly recurring revenue at a time: paying customers (active or past due) at today's prices.</summary>
    public static int Mrr(
        IEnumerable<User> customers, IReadOnlyCollection<AuditEntry> changes, IReadOnlyDictionary<PlanKey, int> prices, DateTimeOffset at)
    {
        var total = 0;
        foreach (var u in customers.Where(u => u.Status is CustomerStatus.Active or CustomerStatus.PastDue))
            if (PlanAt(u, changes, at) is { } plan && plan != PlanKey.Free)
                total += Pricing.PerMonth(prices.GetValueOrDefault(plan), u.Cycle);
        return total;
    }

    /// <summary>
    /// Share (%) of the customers paying at <paramref name="from"/> who are on Free at <paramref name="to"/>.
    /// When the window ends now, customers suspended or banned since count as lost too.
    /// </summary>
    public static double Churn(
        IEnumerable<User> customers, IReadOnlyCollection<AuditEntry> changes, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var paying = customers.Where(u => PlanAt(u, changes, from) is { } p && p != PlanKey.Free).ToList();
        if (paying.Count == 0) return 0;
        var lost = paying.Count(u => PlanAt(u, changes, to) == PlanKey.Free || (to >= now && u.IsBlocked));
        return Math.Round(100.0 * lost / paying.Count, 1);
    }

    /// <summary>Success share (%) of finished posts, or null when none finished.</summary>
    public static double? SuccessRate(int succeeded, int failed) =>
        succeeded + failed == 0 ? null : Math.Round(100.0 * succeeded / (succeeded + failed), 1);
}
