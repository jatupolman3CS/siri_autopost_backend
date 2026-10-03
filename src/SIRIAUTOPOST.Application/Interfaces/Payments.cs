using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.Interfaces;

// The payment provider (Stripe) as the Application sees it. Everything here is neutral: amounts are baht,
// times are DateTimeOffset, and the Stripe SDK stays in Infrastructure.

public enum SubscriptionState
{
    /// <summary>Paid up (also a subscription whose billing the admin paused).</summary>
    Active,
    /// <summary>The last renewal failed; Stripe keeps retrying.</summary>
    PastDue,
    /// <summary>Over: cancelled, or unpaid after the retries.</summary>
    Canceled,
    /// <summary>Created, first payment not made yet. Not a plan yet.</summary>
    Incomplete,
}

/// <param name="Plan">The plan we put in the subscription's metadata; null for a subscription that is not ours.</param>
/// <param name="RenewsAt">The end of the current period (or when a scheduled cancellation takes effect).</param>
/// <param name="CancelAtPeriodEnd">Cancelled to end at <paramref name="RenewsAt"/>.</param>
public sealed record SubscriptionSnapshot(
    string Id, string CustomerId, SubscriptionState State, PlanKey? Plan, BillingCycle? Cycle, DateTimeOffset? RenewsAt, bool CancelAtPeriodEnd);

/// <param name="Price">The plan's price per month, baht (the yearly rate is worked out from it).</param>
/// <param name="FirstDiscount">Baht a promo code takes off the first invoice (0 = none).</param>
public sealed record CheckoutRequest(
    Guid UserId, string CustomerId, PlanKey Plan, BillingCycle Cycle, int Price, int FirstDiscount, string? PromoCode,
    string SuccessUrl, string CancelUrl);

/// <param name="Paid">The session is complete and its first payment went through (or nothing was due).</param>
/// <param name="UserId">The user the session was created for (client_reference_id).</param>
public sealed record CheckoutSessionSnapshot(
    string Id, string? CustomerId, string? SubscriptionId, bool Paid, Guid? UserId, string? PromoCode);

public sealed record CardSnapshot(string Brand, string Last4, int ExpMonth, int ExpYear);

/// <param name="Amount">Baht the invoice charged (paid) or asked for (failed).</param>
/// <param name="IsFirst">The first invoice of a new subscription (its Checkout shows a failure itself).</param>
public sealed record InvoiceSnapshot(
    string Id, string CustomerId, string? SubscriptionId, decimal Amount, DateTimeOffset At, string? ReceiptUrl, bool IsFirst,
    PlanKey? Plan, BillingCycle? Cycle, string? PromoCode);

/// <param name="Succeeded">False when the refund failed or was cancelled (nothing went back).</param>
public sealed record RefundSnapshot(string Id, string? PaymentIntentId, decimal Amount, bool Succeeded, DateTimeOffset At);

/// <summary>What a verified webhook told us. Handlers re-read the subscription from Stripe, so a late or reordered event cannot undo a newer one.</summary>
public abstract record PaymentEvent(string Id);

public sealed record CheckoutCompletedEvent(string Id, CheckoutSessionSnapshot Session) : PaymentEvent(Id);

public sealed record SubscriptionChangedEvent(string Id, string SubscriptionId, string CustomerId) : PaymentEvent(Id);

public sealed record InvoicePaidEvent(string Id, InvoiceSnapshot Invoice) : PaymentEvent(Id);

public sealed record InvoiceFailedEvent(string Id, InvoiceSnapshot Invoice) : PaymentEvent(Id);

public sealed record RefundEvent(string Id, RefundSnapshot Refund) : PaymentEvent(Id);

public interface IPaymentGateway
{
    /// <summary>False until Stripe:SecretKey is configured: plans cannot be bought, and nothing here may be called.</summary>
    bool Enabled { get; }

    /// <summary>The user's Stripe customer; created (once, idempotently) when the user has none yet.</summary>
    Task<string> EnsureCustomerAsync(User user, CancellationToken ct = default);

    /// <summary>A Checkout session for a new subscription; returns the page to send the customer to.</summary>
    Task<string> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default);

    Task<CheckoutSessionSnapshot> GetCheckoutSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>The Billing Portal page where the customer updates the card, sees invoices and cancels.</summary>
    Task<string> CreatePortalAsync(string customerId, string returnUrl, CancellationToken ct = default);

    Task<SubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default);

    /// <summary>Moves a live subscription to another plan or cycle now; the difference is prorated and invoiced at once. Fails when the card declines.</summary>
    Task<SubscriptionSnapshot> ChangeSubscriptionAsync(string subscriptionId, PlanKey plan, BillingCycle cycle, int price, CancellationToken ct = default);

    /// <summary>Schedules the subscription to end with the period (or takes the schedule back).</summary>
    Task<SubscriptionSnapshot> SetCancelAtPeriodEndAsync(string subscriptionId, bool cancel, CancellationToken ct = default);

    /// <summary>Stops (or resumes) collecting payments on a subscription, for suspended customers.</summary>
    Task SetBillingPausedAsync(string subscriptionId, bool paused, CancellationToken ct = default);

    /// <summary>The customer's default card, or null when none (or Stripe could not be asked).</summary>
    Task<CardSnapshot?> GetCardAsync(string customerId, CancellationToken ct = default);

    /// <summary>The payment intent that paid an invoice; null when nothing was charged.</summary>
    Task<string?> GetInvoicePaymentIntentAsync(string invoiceId, CancellationToken ct = default);

    /// <summary>Refunds <paramref name="amount"/> baht of a payment. The key makes a retry safe.</summary>
    Task<RefundSnapshot> RefundAsync(string paymentIntentId, decimal amount, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Asks Stripe to collect an open invoice with the customer's current card.</summary>
    Task<InvoiceSnapshot> PayInvoiceAsync(string invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Checks the Stripe-Signature of a webhook body and turns the events we act on into <see cref="PaymentEvent"/>s;
    /// null for every other event type. Throws <see cref="Domain.Exceptions.InvalidWebhookException"/> when the signature is wrong.
    /// </summary>
    PaymentEvent? ParseWebhook(string payload, string? signature);
}

/// <summary>The address of the web app, for the pages Stripe sends the customer back to.</summary>
public interface IAppUrls
{
    /// <summary>The origin of the web app that made this request (or Stripe:ReturnBaseUrl), without a trailing slash.</summary>
    string WebBase { get; }
}
