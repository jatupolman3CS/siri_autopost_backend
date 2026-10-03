using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class PlatformMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<PlanKey, int> Prices = new()
    {
        [PlanKey.Free] = 0, [PlanKey.Basic] = 290, [PlanKey.Pro] = 790, [PlanKey.Agency] = 1990,
    };

    private static User Customer(PlanKey plan, DateTimeOffset since)
    {
        var u = User.Create($"{Guid.NewGuid():N}@shop.co", "c", UserRole.User, PlanKey.Free, since);
        if (plan != PlanKey.Free) Subscribe(u, plan, BillingCycle.Month);
        return u;
    }

    private static void Subscribe(User u, PlanKey plan, BillingCycle cycle) =>
        u.ApplySubscription($"sub_{u.Id:N}", plan, cycle, null, cancelAtPeriodEnd: false, pastDue: false);

    /// <summary>Moves the customer to a plan and returns the log entry the handlers would write.</summary>
    private static AuditEntry Move(User u, PlanKey to, DateTimeOffset at)
    {
        var from = u.Plan;
        if (to == PlanKey.Free) u.EndSubscription();
        else Subscribe(u, to, BillingCycle.Month);
        return AuditEntry.PlanChange(u.Id, u, from, at);
    }

    [Fact]
    public void The_plan_at_a_time_is_rebuilt_from_later_changes()
    {
        var u = Customer(PlanKey.Basic, Now.AddDays(-90));
        var log = new[] { Move(u, PlanKey.Pro, Now.AddDays(-40)), Move(u, PlanKey.Free, Now.AddDays(-5)) };
        Assert.Equal(PlanKey.Basic, PlatformMetrics.PlanAt(u, log, Now.AddDays(-60)));
        Assert.Equal(PlanKey.Pro, PlatformMetrics.PlanAt(u, log, Now.AddDays(-30)));
        Assert.Equal(PlanKey.Free, PlatformMetrics.PlanAt(u, log, Now));
        Assert.Null(PlatformMetrics.PlanAt(u, log, Now.AddDays(-100))); // not signed up yet
    }

    [Fact]
    public void Mrr_then_and_now_counts_paying_customers_at_monthly_prices()
    {
        var stays = Customer(PlanKey.Pro, Now.AddDays(-90));
        var leaves = Customer(PlanKey.Agency, Now.AddDays(-90));
        var joins = Customer(PlanKey.Basic, Now.AddDays(-3));
        var yearly = Customer(PlanKey.Pro, Now.AddDays(-90));
        Subscribe(yearly, PlanKey.Pro, BillingCycle.Year);
        var log = new[] { Move(leaves, PlanKey.Free, Now.AddDays(-10)) };
        var all = new[] { stays, leaves, joins, yearly };

        Assert.Equal(790 + 290 + 632, PlatformMetrics.Mrr(all, log, Prices, Now));
        Assert.Equal(790 + 1990 + 632, PlatformMetrics.Mrr(all, log, Prices, Now.AddDays(-30)));
    }

    [Fact]
    public void Churn_is_the_share_of_paying_customers_lost_in_the_window()
    {
        var a = Customer(PlanKey.Pro, Now.AddDays(-90));
        var b = Customer(PlanKey.Pro, Now.AddDays(-90));
        var c = Customer(PlanKey.Basic, Now.AddDays(-90));
        var d = Customer(PlanKey.Basic, Now.AddDays(-90));
        var log = new[] { Move(a, PlanKey.Free, Now.AddDays(-10)), Move(b, PlanKey.Free, Now.AddDays(-45)) };
        d.SetStatus(CustomerStatus.Banned);
        var all = new[] { a, b, c, d };

        // Last 30 days: a went free, d was banned, out of a, c and d paying 30 days ago.
        Assert.Equal(66.7, PlatformMetrics.Churn(all, log, Now.AddDays(-30), Now, Now));
        // The 30 days before: only b, out of all four; bans are not dated, so they count only in the current window.
        Assert.Equal(25, PlatformMetrics.Churn(all, log, Now.AddDays(-60), Now.AddDays(-30), Now));
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(19, 1, 95.0)]
    [InlineData(2, 1, 66.7)]
    public void Success_rate_ignores_empty_windows(int ok, int failed, double? expected) =>
        Assert.Equal(expected, PlatformMetrics.SuccessRate(ok, failed));
}
