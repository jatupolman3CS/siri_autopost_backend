using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(790, BillingCycle.Month, null, 790)]
    [InlineData(790, BillingCycle.Year, null, 632 * 12)]   // 80% of 790 = 632 a month
    [InlineData(790, BillingCycle.Month, "d20", 632)]
    [InlineData(290, BillingCycle.Month, "dFree", 0)]
    [InlineData(1990, BillingCycle.Year, "dFree", 1592 * 11)]
    public void Charges_follow_the_cycle_and_the_promo(int price, BillingCycle cycle, string? promo, int expected) =>
        Assert.Equal(expected, Pricing.Charge(price, cycle, promo));

    [Fact]
    public void Customer_overrides_win_over_the_plan_and_zero_means_unlimited()
    {
        var pro = PlanSetting.Defaults.Single(p => p.Key == PlanKey.Pro);
        Assert.Equal(new EffectiveLimits(10, 300, 3, 3, 300, 1000, 1000), EffectiveLimits.Of(pro, null));
        var limits = EffectiveLimits.Of(pro, new LimitOverrides { Devices = 5, Accounts = 0 });
        Assert.Equal((null, 5), (limits.Accounts, limits.Devices));
    }

    [Theory]
    [InlineData(790, BillingCycle.Month, 790)]
    [InlineData(790, BillingCycle.Year, 632 * 12)]
    public void A_period_costs_the_monthly_rate_times_its_months(int price, BillingCycle cycle, int expected) =>
        Assert.Equal(expected, Pricing.Period(price, cycle));

    [Theory]
    [InlineData(790, BillingCycle.Month, "d20", 158)]
    [InlineData(790, BillingCycle.Month, null, 0)]
    [InlineData(290, BillingCycle.Month, "dFree", 290)]       // the whole first month
    [InlineData(1990, BillingCycle.Year, "dFree", 1592)]      // one month of the yearly rate
    public void A_promo_takes_this_much_off_the_first_invoice(int price, BillingCycle cycle, string? promo, int off) =>
        Assert.Equal(off, Pricing.FirstDiscount(price, cycle, promo));

    [Fact]
    public void A_new_account_is_active_on_its_plan_and_a_subscription_decides_the_plan_afterwards()
    {
        var u = User.Create("a@shop.co", "", UserRole.User, PlanKey.Free, Now);
        Assert.Equal(CustomerStatus.Active, u.Status);
        Assert.False(u.HasSubscription);

        var renews = Now.AddMonths(1);
        u.ApplySubscription("sub_1", PlanKey.Pro, BillingCycle.Year, renews, cancelAtPeriodEnd: false, pastDue: false);
        Assert.Equal((PlanKey.Pro, BillingCycle.Year, "sub_1", renews), (u.Plan, u.Cycle, u.StripeSubscriptionId, u.PlanRenewsAt));
        Assert.True(u.HasSubscription);

        u.MarkPastDue();
        Assert.Equal(CustomerStatus.PastDue, u.Status);
        u.MarkPaid();
        Assert.Equal(CustomerStatus.Active, u.Status);

        u.ApplySubscription("sub_1", PlanKey.Pro, BillingCycle.Year, renews, cancelAtPeriodEnd: true, pastDue: false);
        Assert.True(u.CancelAtPeriodEnd);
        Assert.Equal(PlanKey.Pro, u.Plan); // stays until the period ends

        u.EndSubscription();
        Assert.Equal((PlanKey.Free, false, (string?)null, (DateTimeOffset?)null), (u.Plan, u.CancelAtPeriodEnd, u.StripeSubscriptionId, u.PlanRenewsAt));
    }

    [Fact]
    public void Stripe_never_lifts_a_block_and_the_admin_cannot_move_a_paid_plan()
    {
        var u = User.Create("a@shop.co", "", UserRole.User, PlanKey.Free, Now);
        u.ApplySubscription("sub_1", PlanKey.Basic, BillingCycle.Month, null, false, false);
        u.SetStatus(CustomerStatus.Suspended);
        u.ApplySubscription("sub_1", PlanKey.Pro, BillingCycle.Month, null, false, pastDue: false);
        Assert.Equal(CustomerStatus.Suspended, u.Status);

        Assert.Throws<DomainException>(() => u.SetPlanByAdmin(PlanKey.Agency));
        Assert.Throws<DomainException>(() => u.DowngradeToFree());
        u.EndSubscription();
        u.SetPlanByAdmin(PlanKey.Agency); // a plan the admin grants
        Assert.Equal(PlanKey.Agency, u.Plan);
        u.DowngradeToFree();
        Assert.Equal(PlanKey.Free, u.Plan);
    }

    [Fact]
    public void Suspending_blocks_and_pauses_and_restoring_lifts_both()
    {
        var u = User.Create("a@shop.co", "", UserRole.User, PlanKey.Free, Now);
        u.SetStatus(CustomerStatus.Suspended);
        Assert.True(u.IsBlocked);
        Assert.False(u.CanPost);
        u.SetStatus(CustomerStatus.Active);
        Assert.True(u.CanPost);
        var admin = User.Create("root@x.co", "", UserRole.Admin, PlanKey.Agency, Now);
        Assert.Throws<DomainException>(() => admin.SetStatus(CustomerStatus.Banned));
    }

    [Fact]
    public void A_charge_is_refunded_against_its_payment_and_a_failed_invoice_can_be_paid_later()
    {
        var charge = Transaction.Charge(Guid.NewGuid(), 790, PlanKey.Pro, BillingCycle.Month, null, Now, new PaymentRefs("in_1", "pi_1", "https://invoice"));
        Assert.True(charge.CanRefund);
        var refund = charge.RefundOf(Now.AddDays(1), stripeRefundId: "re_1");
        Assert.Equal((TransactionType.Refund, 790m, charge.Id, "re_1", "pi_1"), (refund.Type, refund.Amount, refund.RefundOfId, refund.StripeRefundId, refund.StripePaymentIntentId));
        Assert.Throws<DomainException>(() => refund.RefundOf(Now));
        Assert.Throws<DomainException>(() => charge.RefundOf(Now, 0));
        Assert.Throws<DomainException>(() => charge.RefundOf(Now, 790.01m));
        Assert.Equal(100m, charge.RefundOf(Now, 100).Amount); // a partial refund

        var legacy = Transaction.Charge(Guid.NewGuid(), 790, PlanKey.Pro, BillingCycle.Month, null, Now); // before Stripe: no payment behind it
        Assert.False(legacy.CanRefund);
        Assert.False(Transaction.Charge(Guid.NewGuid(), 0, PlanKey.Pro, BillingCycle.Month, null, Now, new PaymentRefs("in_0", "pi_0")).CanRefund);

        var failed = Transaction.FailedCharge(Guid.NewGuid(), 290, PlanKey.Basic, BillingCycle.Month, Now, new PaymentRefs("in_2"));
        Assert.Throws<DomainException>(() => failed.RefundOf(Now));
        failed.MarkPaid(Now.AddDays(2), 290, new PaymentRefs("in_2", "pi_2", "https://invoice-2"));
        Assert.Equal((TransactionType.Charge, "in_2", "pi_2", true), (failed.Type, failed.StripeInvoiceId, failed.StripePaymentIntentId, failed.CanRefund));
    }

    [Fact]
    public void Promo_codes_are_normalized_and_stop_working_when_closed_or_expired()
    {
        var p = Promo.Create(" launch20 ", "d20", Now.AddDays(10));
        Assert.Equal("LAUNCH20", p.Code);
        p.EnsureUsable(Now);
        p.RecordUse();
        Assert.Equal(1, p.Uses);
        Assert.Throws<DomainException>(() => p.EnsureUsable(Now.AddDays(11)));
        p.SetActive(false);
        Assert.Throws<DomainException>(() => p.EnsureUsable(Now));
        Assert.Throws<DomainException>(() => Promo.Create("x!", "d20", Now));
        Assert.Throws<DomainException>(() => Promo.Create("GOOD", "d50", Now));
    }

    [Fact]
    public void Plan_settings_reject_bad_values()
    {
        var free = PlanSetting.Defaults.Single(p => p.Key == PlanKey.Free);
        Assert.Throws<DomainException>(() => free.Update(10, 1, 10, 1, 1));
        var cheap = PlanSetting.Defaults.Single(p => p.Key == PlanKey.Basic);
        Assert.Throws<DomainException>(() => cheap.Update(PlanSetting.MinPaidPrice - 1, 2, 30, 1, 1)); // Stripe cannot charge less
        cheap.Update(PlanSetting.MinPaidPrice, 2, 30, 1, 1);
        var basic = PlanSetting.Defaults.Single(p => p.Key == PlanKey.Basic);
        Assert.Throws<DomainException>(() => basic.Update(290, 0, 30, 1, 1));
        basic.Update(350, null, 50, 2, 1);
        Assert.Equal((350, (int?)null), (basic.Price, basic.Accounts));
    }

    [Fact]
    public void Members_join_now_when_the_email_has_an_account()
    {
        var existing = User.Create("b@shop.co", "", UserRole.User, PlanKey.Free, Now);
        var joined = WorkspaceMember.Invite(Guid.NewGuid(), " B@Shop.co", WorkspaceRole.Editor, existing, Now);
        Assert.True(joined.IsActive);
        var pending = WorkspaceMember.Invite(Guid.NewGuid(), "c@shop.co", WorkspaceRole.Viewer, null, Now);
        Assert.False(pending.IsActive);
        Assert.Throws<DomainException>(() => WorkspaceMember.Invite(Guid.NewGuid(), "d@shop.co", WorkspaceRole.Owner, null, Now));
    }
}
