using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Entities;

/// <summary>
/// One try to pay for a plan in the in-app checkout, tied to a Stripe PaymentIntent. The row is what the
/// webhook (payment_intent.*) and the checkout's own status calls update, so the server always knows if a
/// payment is pending, paid or failed, and fulfils a paid one exactly once. A failed attempt can still turn into
/// a paid one: the customer may try another card on the same PaymentIntent.
/// </summary>
public class PaymentAttempt : Entity
{
    public Guid UserId { get; private set; }
    /// <summary>The Stripe PaymentIntent (pi_...): of the subscription's first invoice, or the one-off PromptPay payment. Unique.</summary>
    public string StripePaymentIntentId { get; private set; } = "";
    /// <summary>The subscription an incomplete one was made for (Subscription flow only).</summary>
    public string? StripeSubscriptionId { get; private set; }
    public PaymentFlow Flow { get; private set; }
    /// <summary>The row the customer picked.</summary>
    public PaymentMethodKind RequestedMethod { get; private set; }
    /// <summary>How it was really paid (what Stripe saw); null until a method is used.</summary>
    public PaymentMethodKind? PaidMethod { get; private set; }
    public PlanKey Plan { get; private set; }
    public BillingCycle Cycle { get; private set; }
    /// <summary>Baht to pay, promo code and test amount included.</summary>
    public decimal Amount { get; private set; }
    public string? PromoCode { get; private set; }
    public PaymentAttemptState State { get; private set; }
    public string? FailureMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }

    private PaymentAttempt() { } // EF Core

    public static PaymentAttempt Start(
        Guid userId, string paymentIntentId, string? subscriptionId, PaymentFlow flow, PaymentMethodKind requested,
        PlanKey plan, BillingCycle cycle, decimal amount, string? promoCode, DateTimeOffset now) =>
        new()
        {
            UserId = userId, StripePaymentIntentId = paymentIntentId, StripeSubscriptionId = subscriptionId, Flow = flow,
            RequestedMethod = requested, Plan = plan, Cycle = cycle, Amount = amount, PromoCode = promoCode,
            State = PaymentAttemptState.Pending, CreatedAt = now, UpdatedAt = now,
        };

    /// <summary>Waiting for the customer or the bank (a QR not scanned yet, a payment processing).</summary>
    public void MarkPending(PaymentMethodKind? method, DateTimeOffset now)
    {
        if (State == PaymentAttemptState.Succeeded) return;
        State = PaymentAttemptState.Pending;
        FailureMessage = null;
        Touch(method, now);
    }

    public void MarkFailed(string? message, PaymentMethodKind? method, DateTimeOffset now)
    {
        if (State == PaymentAttemptState.Succeeded) return; // a late event must not undo a payment
        State = PaymentAttemptState.Failed;
        FailureMessage = message is { Length: > 500 } ? message[..500] : message;
        Touch(method, now);
    }

    /// <summary>True the first time only: that is the moment to hand over what was paid for.</summary>
    public bool MarkSucceeded(PaymentMethodKind? method, DateTimeOffset now)
    {
        if (State == PaymentAttemptState.Succeeded) return false;
        State = PaymentAttemptState.Succeeded;
        FailureMessage = null;
        SettledAt = now;
        Touch(method, now);
        return true;
    }

    private void Touch(PaymentMethodKind? method, DateTimeOffset now)
    {
        PaidMethod = method ?? PaidMethod;
        UpdatedAt = now;
    }
}
