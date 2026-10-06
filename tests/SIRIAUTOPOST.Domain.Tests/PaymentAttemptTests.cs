using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

// The in-app checkout's records: one payment attempt per Stripe PaymentIntent, and the prepaid plan (PromptPay).
public class PaymentAttemptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static PaymentAttempt Attempt(PaymentFlow flow = PaymentFlow.Prepaid) =>
        PaymentAttempt.Start(Guid.NewGuid(), "pi_1", flow == PaymentFlow.Subscription ? "sub_1" : null, flow, PaymentMethodKind.Promptpay,
            PlanKey.Pro, BillingCycle.Month, 790m, null, Now);

    private static User Customer() => User.Create("buyer@shop.co", "Buyer", UserRole.User, PlanKey.Free, Now);

    [Fact]
    public void An_attempt_starts_pending_and_succeeds_once()
    {
        var a = Attempt();
        Assert.Equal(PaymentAttemptState.Pending, a.State);

        Assert.True(a.MarkSucceeded(PaymentMethodKind.Promptpay, Now.AddMinutes(1)));
        Assert.Equal((PaymentAttemptState.Succeeded, PaymentMethodKind.Promptpay, Now.AddMinutes(1)), (a.State, a.PaidMethod, a.SettledAt));
        Assert.False(a.MarkSucceeded(PaymentMethodKind.Card, Now.AddMinutes(2))); // the hand-over happens only the first time
        Assert.Equal(PaymentMethodKind.Promptpay, a.PaidMethod);
    }

    [Fact]
    public void A_failed_attempt_can_still_succeed_but_a_paid_one_never_goes_back()
    {
        var a = Attempt();
        a.MarkFailed("Your card was declined.", PaymentMethodKind.Card, Now);
        Assert.Equal((PaymentAttemptState.Failed, "Your card was declined."), (a.State, a.FailureMessage));

        a.MarkPending(PaymentMethodKind.Card, Now.AddSeconds(5)); // the customer is trying again
        Assert.Equal((PaymentAttemptState.Pending, (string?)null), (a.State, a.FailureMessage));

        Assert.True(a.MarkSucceeded(PaymentMethodKind.Card, Now.AddSeconds(30)));
        a.MarkFailed("late event", null, Now.AddMinutes(5));
        a.MarkPending(null, Now.AddMinutes(6));
        Assert.Equal((PaymentAttemptState.Succeeded, (string?)null, PaymentMethodKind.Card), (a.State, a.FailureMessage, a.PaidMethod));
    }

    [Fact]
    public void A_failure_message_is_kept_short_and_the_method_stays_when_a_later_call_does_not_know_it()
    {
        var a = Attempt();
        a.MarkFailed(new string('x', 900), PaymentMethodKind.Link, Now);
        Assert.Equal(500, a.FailureMessage!.Length);
        a.MarkFailed("again", null, Now.AddSeconds(1));
        Assert.Equal(PaymentMethodKind.Link, a.PaidMethod);
    }

    [Fact]
    public void A_prepaid_plan_has_an_end_date_and_nothing_renews_it()
    {
        var u = Customer();
        Assert.False(u.IsPrepaid);

        u.ApplyPrepaid(PlanKey.Pro, BillingCycle.Month, Now.AddMonths(1));
        Assert.Equal((PlanKey.Pro, BillingCycle.Month, true, false), (u.Plan, u.Cycle, u.IsPrepaid, u.HasSubscription));
        Assert.False(u.PrepaidEnded(Now.AddDays(30)));
        Assert.True(u.PrepaidEnded(Now.AddMonths(1)));

        u.EndPrepaid();
        Assert.Equal((PlanKey.Free, (DateTimeOffset?)null, false), (u.Plan, u.PlanRenewsAt, u.IsPrepaid));
    }

    [Fact]
    public void A_plan_the_admin_granted_is_not_prepaid_and_granting_one_clears_a_prepaid_end_date()
    {
        var granted = Customer();
        granted.SetPlanByAdmin(PlanKey.Agency);
        Assert.False(granted.IsPrepaid);
        Assert.False(granted.PrepaidEnded(Now.AddYears(5)));

        var u = Customer();
        u.ApplyPrepaid(PlanKey.Basic, BillingCycle.Month, Now.AddMonths(1));
        u.SetPlanByAdmin(PlanKey.Pro);
        Assert.Equal((PlanKey.Pro, (DateTimeOffset?)null, false), (u.Plan, u.PlanRenewsAt, u.IsPrepaid));
    }

    [Fact]
    public void A_subscriber_cannot_be_given_a_prepaid_period_and_ending_one_leaves_a_subscription_alone()
    {
        var u = Customer();
        u.ApplySubscription("sub_1", PlanKey.Pro, BillingCycle.Month, Now.AddDays(10), false, false);

        Assert.Throws<DomainException>(() => u.ApplyPrepaid(PlanKey.Agency, BillingCycle.Month, Now.AddMonths(1)));
        u.EndPrepaid();
        Assert.Equal((PlanKey.Pro, true), (u.Plan, u.HasSubscription));
        Assert.False(u.PrepaidEnded(Now.AddYears(1)));
    }

    [Fact]
    public void Paying_again_brings_a_past_due_customer_back_to_active()
    {
        var u = Customer();
        u.ApplySubscription("sub_1", PlanKey.Pro, BillingCycle.Month, Now, false, pastDue: true);
        u.EndSubscription();
        Assert.Equal(CustomerStatus.Active, u.Status);

        u.ApplyPrepaid(PlanKey.Basic, BillingCycle.Year, Now.AddYears(1));
        Assert.Equal((PlanKey.Basic, BillingCycle.Year, CustomerStatus.Active), (u.Plan, u.Cycle, u.Status));
    }
}
