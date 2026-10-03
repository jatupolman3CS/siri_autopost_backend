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
        Assert.Equal(new EffectiveLimits(10, null, 3, 1), EffectiveLimits.Of(pro, null));
        var limits = EffectiveLimits.Of(pro, new LimitOverrides { Devices = 5, Accounts = 0 });
        Assert.Equal((null, 5), (limits.Accounts, limits.Devices));
    }

    [Fact]
    public void A_paid_sign_up_is_a_trial_until_the_first_charge()
    {
        var u = User.Create("a@shop.co", "", UserRole.User, PlanKey.Pro, Now);
        Assert.Equal(CustomerStatus.Trial, u.Status);
        u.ChangePlan(PlanKey.Pro, BillingCycle.Year, paid: true);
        Assert.Equal((CustomerStatus.Active, BillingCycle.Year), (u.Status, u.Cycle));
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
    public void A_charge_is_refunded_once_and_a_failed_charge_can_be_recorded_as_paid()
    {
        var charge = Transaction.Charge(Guid.NewGuid(), 790, PlanKey.Pro, BillingCycle.Month, null, Now);
        var refund = charge.RefundOf(Now.AddDays(1));
        Assert.Equal((TransactionType.Refund, 790, charge.Id), (refund.Type, refund.Amount, refund.RefundOfId));
        Assert.Throws<DomainException>(() => refund.RefundOf(Now));

        var failed = Transaction.FailedCharge(Guid.NewGuid(), 290, PlanKey.Basic, BillingCycle.Month, Now);
        failed.MarkPaid(Now.AddDays(2));
        Assert.Equal(TransactionType.Charge, failed.Type);
    }

    [Fact]
    public void Promo_codes_are_normalized_and_stop_working_when_closed_or_expired()
    {
        var p = Promo.Create(" launch20 ", "d20", Now.AddDays(10));
        Assert.Equal("LAUNCH20", p.Code);
        p.Use(Now);
        Assert.Equal(1, p.Uses);
        Assert.Throws<DomainException>(() => p.Use(Now.AddDays(11)));
        p.SetActive(false);
        Assert.Throws<DomainException>(() => p.Use(Now));
        Assert.Throws<DomainException>(() => Promo.Create("x!", "d20", Now));
        Assert.Throws<DomainException>(() => Promo.Create("GOOD", "d50", Now));
    }

    [Fact]
    public void Plan_settings_reject_bad_values()
    {
        var free = PlanSetting.Defaults.Single(p => p.Key == PlanKey.Free);
        Assert.Throws<DomainException>(() => free.Update(10, 1, 10, 1, 1));
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
