using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class BillingProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static User Customer() => User.Create("a@shop.co", "A", UserRole.User, PlanKey.Free, Now);

    [Fact]
    public void A_new_customer_has_no_card_and_the_default_notifications()
    {
        var b = Customer().Billing;
        Assert.False(b.HasCard);
        Assert.Equal((true, true, false), (b.NotifyFailed, b.NotifyExpiring, b.NotifyRenewal));
    }

    [Fact]
    public void A_card_keeps_only_brand_last_four_and_expiry()
    {
        var u = Customer();
        u.SetPaymentMethod("VISA", "4242", 11, 2026, Now);
        Assert.Equal(("visa", "4242", 11, 2026), (u.Billing.CardBrand, u.Billing.CardLast4, u.Billing.CardExpMonth, u.Billing.CardExpYear));
        // Valid through the end of the expiry month.
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), u.Billing.CardValidThrough);
    }

    [Fact]
    public void An_unknown_brand_is_just_a_card()
    {
        var u = Customer();
        u.SetPaymentMethod("<script>", "4242", 12, 2030, Now);
        Assert.Equal("card", u.Billing.CardBrand);
    }

    [Theory]
    [InlineData("424", 11, 2026, "4 ตัวท้าย")]
    [InlineData("42a2", 11, 2026, "4 ตัวท้าย")]
    [InlineData("4242", 13, 2026, "เดือน")]
    [InlineData("4242", 0, 2026, "เดือน")]
    [InlineData("4242", 9, 2026, "หมดอายุแล้ว")]   // last valid moment was the end of September
    [InlineData("4242", 10, 1999, "ปี")]
    public void Bad_cards_are_refused(string last4, int month, int year, string reason)
    {
        var ex = Assert.Throws<DomainException>(() => Customer().SetPaymentMethod("visa", last4, month, year, Now));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void The_current_month_is_still_valid()
    {
        var u = Customer();
        u.SetPaymentMethod("visa", "4242", 10, 2026, Now);
        Assert.True(u.Billing.HasCard);
    }

    [Fact]
    public void Notifications_and_the_card_change_independently()
    {
        var u = Customer();
        u.SetPaymentMethod("visa", "4242", 12, 2030, Now);
        u.SetBillingNotifications(false, true, true);
        Assert.Equal(("4242", false, true, true), (u.Billing.CardLast4, u.Billing.NotifyFailed, u.Billing.NotifyExpiring, u.Billing.NotifyRenewal));
        u.RemovePaymentMethod();
        Assert.Equal((false, null, false), (u.Billing.HasCard, u.Billing.CardBrand, u.Billing.NotifyFailed));
    }

    [Fact]
    public void A_change_is_a_new_object_so_EF_sees_it()
    {
        var u = Customer();
        var before = u.Billing;
        u.SetBillingNotifications(false, false, false);
        Assert.NotSame(before, u.Billing);
        Assert.True(before.NotifyFailed); // the old object is untouched
    }
}
