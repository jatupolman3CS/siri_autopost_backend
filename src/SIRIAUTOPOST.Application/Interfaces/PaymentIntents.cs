using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.Interfaces;

// The in-app checkout's side of the payment provider: a PaymentIntent the browser confirms with Stripe.js.

/// <param name="ChargeOverride">The admin's test amount (<see cref="Domain.Entities.PaymentOverride"/>) for a period instead of the plan's price.</param>
/// <param name="AttemptId">Makes a retried request create the same Stripe objects once (the idempotency key).</param>
public sealed record SubscriptionPaymentRequest(
    Guid UserId, string CustomerId, PlanKey Plan, BillingCycle Cycle, int Price, int FirstDiscount, string? PromoCode,
    int? ChargeOverride, Guid AttemptId);

/// <param name="Amount">Baht for the one period, after the promo code or the test amount.</param>
public sealed record PrepaidPaymentRequest(
    Guid UserId, string CustomerId, string Email, PlanKey Plan, BillingCycle Cycle, decimal Amount, string? PromoCode, Guid AttemptId);

/// <param name="IntentId">The PaymentIntent to confirm in the browser (pi_...).</param>
/// <param name="ClientSecret">What Stripe.js needs to confirm it; safe to hand to the customer who is paying.</param>
/// <param name="Amount">Baht that will be charged now (Stripe's own figure for a subscription's first invoice).</param>
/// <param name="SubscriptionId">The incomplete subscription behind it (Subscription flow); it starts when the payment succeeds.</param>
public sealed record StartedPayment(string IntentId, string ClientSecret, decimal Amount, string? SubscriptionId);

public enum PaymentIntentState
{
    /// <summary>Not paid yet: waiting for the customer, the bank, or still being processed.</summary>
    Pending,
    Succeeded,
    /// <summary>Declined, expired or cancelled. The customer may still try again on the same intent.</summary>
    Failed,
}

/// <param name="Method">How it was paid, from the charge Stripe made; null before any attempt.</param>
public sealed record PaymentIntentSnapshot(
    string Id, PaymentIntentState State, decimal Amount, PaymentMethodKind? Method, string? FailureMessage, string? ReceiptUrl);

/// <summary>A verified webhook about a PaymentIntent. Handlers read the intent again from Stripe, so only its id matters.</summary>
public sealed record PaymentIntentEvent(string Id, string PaymentIntentId) : PaymentEvent(Id);

/// <summary>What the browser needs to run the in-app checkout.</summary>
public interface IPaymentConfig
{
    /// <summary>The publishable key (pk_...); null while payments are off or no key is set (the web app then uses Stripe's own page).</summary>
    string? PublishableKey { get; }

    /// <summary>ISO currency of every price, lower case.</summary>
    string Currency { get; }
}
