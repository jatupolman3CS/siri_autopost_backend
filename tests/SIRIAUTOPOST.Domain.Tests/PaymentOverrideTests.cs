using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class PaymentOverrideTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_setting_is_off_and_charges_nobody_anything_special()
    {
        var p = PaymentOverride.CreateDefault();
        Assert.Equal((false, PlanSetting.MinPaidPrice), (p.Enabled, p.Amount));
        Assert.Null(p.ChargeFor("any@shop.co"));
    }

    [Fact]
    public void Only_the_listed_customers_get_the_test_amount_and_only_while_it_is_on()
    {
        var p = PaymentOverride.CreateDefault();
        p.Update(true, 20, ["Tester@Shop.co ", "tester@shop.co", "", "b@shop.co"], Now);

        Assert.Equal(["tester@shop.co", "b@shop.co"], p.Emails); // trimmed, lower-cased, no blanks or duplicates
        Assert.Equal(20, p.ChargeFor("TESTER@shop.co"));
        Assert.Null(p.ChargeFor("someone.else@shop.co"));

        p.Update(false, 20, ["tester@shop.co"], Now);
        Assert.Null(p.ChargeFor("tester@shop.co"));
        Assert.Equal(["tester@shop.co"], p.Emails); // switching off keeps the list for next time
    }

    [Fact]
    public void Turning_it_on_needs_at_least_one_customer_so_real_customers_never_pay_the_test_amount()
    {
        var p = PaymentOverride.CreateDefault();
        Assert.Throws<DomainException>(() => p.Update(true, 20, [], Now));
        Assert.Throws<DomainException>(() => p.Update(true, 20, ["  "], Now));
        p.Update(false, 20, [], Now); // an empty list is fine while it is off
    }

    [Theory]
    [InlineData(9)] // under what Stripe can charge
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(PaymentOverride.MaxAmount + 1)]
    public void The_amount_must_be_one_that_stripe_can_charge(int amount) =>
        Assert.Throws<DomainException>(() => PaymentOverride.CreateDefault().Update(false, amount, ["a@shop.co"], Now));

    [Theory]
    [InlineData(PlanSetting.MinPaidPrice)]
    [InlineData(PaymentOverride.MaxAmount)]
    public void The_limits_of_the_amount_are_allowed(int amount)
    {
        var p = PaymentOverride.CreateDefault();
        p.Update(true, amount, ["a@shop.co"], Now);
        Assert.Equal(amount, p.ChargeFor("a@shop.co"));
    }

    [Fact]
    public void The_list_is_capped()
    {
        var many = Enumerable.Range(0, PaymentOverride.MaxEmails + 1).Select(i => $"u{i}@shop.co");
        Assert.Throws<DomainException>(() => PaymentOverride.CreateDefault().Update(false, 20, many, Now));
    }

    [Fact]
    public void A_refused_update_changes_nothing()
    {
        var p = PaymentOverride.CreateDefault();
        p.Update(true, 20, ["a@shop.co"], Now);
        Assert.Throws<DomainException>(() => p.Update(true, 5, ["b@shop.co"], Now.AddHours(1)));
        Assert.Equal((true, 20, Now), (p.Enabled, p.Amount, p.UpdatedAt));
        Assert.Equal(["a@shop.co"], p.Emails);
    }
}
