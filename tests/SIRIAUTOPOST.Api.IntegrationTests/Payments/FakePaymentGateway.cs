using System.Collections.Concurrent;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Infrastructure.Payments;

namespace SIRIAUTOPOST.Api.IntegrationTests.Payments;

/// <summary>
/// Stripe for the integration tests: customers, Checkout sessions and subscriptions live in memory and the test
/// plays Stripe's part (pays a session, sends webhooks). Webhook bodies still go through the real
/// <see cref="StripeWebhookParser"/>, so signatures and the event JSON are verified for real.
/// </summary>
public sealed class FakePaymentGateway(string webhookSecret) : IPaymentGateway
{
    private readonly StripeWebhookParser parser = new(webhookSecret);
    private readonly ConcurrentDictionary<Guid, string> customers = new();
    private readonly ConcurrentDictionary<string, CheckoutRequest> sessions = new();
    private readonly ConcurrentDictionary<string, string> sessionSubscriptions = new();
    private readonly ConcurrentDictionary<string, SubscriptionSnapshot> subscriptions = new();
    private readonly ConcurrentDictionary<string, RefundSnapshot> refunds = new();
    private int counter;

    public bool Enabled { get; set; } = true;

    /// <summary>When set, a plan change or an invoice retry is declined with this reason (as a card decline is).</summary>
    public string? Decline { get; set; }

    public ConcurrentQueue<(string Subscription, PlanKey Plan, BillingCycle Cycle, int Price)> Changes { get; } = new();
    public ConcurrentQueue<(string Subscription, bool Paused)> Pauses { get; } = new();
    public ConcurrentQueue<(string PaymentIntent, decimal Amount, string Key)> Refunds { get; } = new();
    public ConcurrentDictionary<string, InvoiceSnapshot> OpenInvoices { get; } = new();

    private string Next(string prefix) => $"{prefix}_{Interlocked.Increment(ref counter):D4}{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    public string CustomerOf(Guid userId) => customers[userId];

    /// <summary>The id of the refund made under an idempotency key (what Stripe would report in refund.created).</summary>
    public string RefundIdOf(string key) => refunds[key].Id;

    public CheckoutRequest Session(string id) => sessions[id];

    public string LastSessionOf(Guid userId) =>
        sessions.Where(s => s.Value.UserId == userId).OrderBy(s => s.Key, StringComparer.Ordinal).Last().Key;

    /// <summary>The customer pays on Stripe's page: the session completes and a subscription starts.</summary>
    public string PayCheckout(string sessionId)
    {
        var r = sessions[sessionId];
        var id = Next("sub");
        subscriptions[id] = new SubscriptionSnapshot(id, r.CustomerId, SubscriptionState.Active, r.Plan, r.Cycle, DateTimeOffset.UtcNow.AddMonths(1), false);
        sessionSubscriptions[sessionId] = id;
        return id;
    }

    public void Set(string subscriptionId, Func<SubscriptionSnapshot, SubscriptionSnapshot> change) =>
        subscriptions[subscriptionId] = change(subscriptions[subscriptionId]);

    public Task<string> EnsureCustomerAsync(User user, CancellationToken ct = default) =>
        Task.FromResult(customers.GetOrAdd(user.Id, _ => Next("cus")));

    public Task<string> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        var id = Next("cs_test");
        sessions[id] = request;
        return Task.FromResult($"https://checkout.stripe.test/c/pay/{id}");
    }

    public Task<CheckoutSessionSnapshot> GetCheckoutSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!sessions.TryGetValue(sessionId, out var r)) throw new PaymentGatewayException("No such session");
        var paid = sessionSubscriptions.TryGetValue(sessionId, out var sub);
        return Task.FromResult(new CheckoutSessionSnapshot(sessionId, r.CustomerId, sub, paid, r.UserId, r.PromoCode));
    }

    public Task<string> CreatePortalAsync(string customerId, string returnUrl, CancellationToken ct = default) =>
        Task.FromResult($"https://billing.stripe.test/p/session/{customerId}?return={Uri.EscapeDataString(returnUrl)}");

    public Task<SubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default) =>
        Task.FromResult(subscriptions.TryGetValue(subscriptionId, out var s) ? s : throw new PaymentGatewayException("No such subscription"));

    public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(string subscriptionId, PlanKey plan, BillingCycle cycle, int price, CancellationToken ct = default)
    {
        if (Decline is { } why) throw new DomainException($"ชำระเงินไม่สำเร็จ: {why}");
        Changes.Enqueue((subscriptionId, plan, cycle, price));
        Set(subscriptionId, s => s with { Plan = plan, Cycle = cycle, CancelAtPeriodEnd = false });
        return Task.FromResult(subscriptions[subscriptionId]);
    }

    public Task<SubscriptionSnapshot> SetCancelAtPeriodEndAsync(string subscriptionId, bool cancel, CancellationToken ct = default)
    {
        Set(subscriptionId, s => s with { CancelAtPeriodEnd = cancel });
        return Task.FromResult(subscriptions[subscriptionId]);
    }

    public Task SetBillingPausedAsync(string subscriptionId, bool paused, CancellationToken ct = default)
    {
        Pauses.Enqueue((subscriptionId, paused));
        return Task.CompletedTask;
    }

    public Task<CardSnapshot?> GetCardAsync(string customerId, CancellationToken ct = default) =>
        Task.FromResult<CardSnapshot?>(customers.Values.Contains(customerId) ? new CardSnapshot("visa", "4242", 12, 2030) : null);

    public Task<string?> GetInvoicePaymentIntentAsync(string invoiceId, CancellationToken ct = default) =>
        Task.FromResult<string?>($"pi_{invoiceId}");

    public Task<RefundSnapshot> RefundAsync(string paymentIntentId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        Refunds.Enqueue((paymentIntentId, amount, idempotencyKey));
        return Task.FromResult(refunds.GetOrAdd(idempotencyKey, _ => new RefundSnapshot(Next("re"), paymentIntentId, amount, true, DateTimeOffset.UtcNow)));
    }

    public Task<InvoiceSnapshot> PayInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        if (Decline is { } why) throw new DomainException($"ชำระเงินไม่สำเร็จ: {why}");
        return Task.FromResult(OpenInvoices.TryGetValue(invoiceId, out var i) ? i : throw new PaymentGatewayException("No such invoice"));
    }

    public PaymentEvent? ParseWebhook(string payload, string? signature) => parser.Parse(payload, signature);
}
