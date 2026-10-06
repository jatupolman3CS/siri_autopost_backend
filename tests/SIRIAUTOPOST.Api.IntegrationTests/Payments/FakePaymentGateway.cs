using System.Collections.Concurrent;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;
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
    private readonly ConcurrentDictionary<string, string> sessionIntents = new();
    private readonly ConcurrentDictionary<string, SubscriptionSnapshot> subscriptions = new();
    private readonly ConcurrentDictionary<string, RefundSnapshot> refunds = new();
    private int counter;

    public bool Enabled { get; set; } = true;

    /// <summary>When set, a plan change or an invoice retry is declined with this reason (as a card decline is).</summary>
    public string? Decline { get; set; }

    public ConcurrentQueue<(string Subscription, PlanKey Plan, BillingCycle Cycle, int Price, int? ChargeOverride)> Changes { get; } = new();
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

    /// <summary>The customer pays on Stripe's page: the session completes and a subscription starts (a one-off PromptPay page: its PaymentIntent is paid, and its id returned).</summary>
    public string PayCheckout(string sessionId)
    {
        var r = sessions[sessionId];
        if (r.PrepaidAmount is { } baht)
        {
            var intent = Make(r.UserId, baht, null, PaymentFlow.Prepaid).IntentId;
            PayIntent(intent, PaymentMethodKind.Promptpay);
            sessionIntents[sessionId] = intent;
            return intent;
        }
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
        if (r.PrepaidAmount is { } baht)
            return Task.FromResult(new CheckoutSessionSnapshot(
                sessionId, r.CustomerId, null, sessionIntents.ContainsKey(sessionId), r.UserId, r.PromoCode,
                sessionIntents.GetValueOrDefault(sessionId), true, r.Plan, r.Cycle, baht));
        return Task.FromResult(new CheckoutSessionSnapshot(sessionId, r.CustomerId, sub, paid, r.UserId, r.PromoCode));
    }

    public Task<string> CreatePortalAsync(string customerId, string returnUrl, CancellationToken ct = default) =>
        Task.FromResult($"https://billing.stripe.test/p/session/{customerId}?return={Uri.EscapeDataString(returnUrl)}");

    public Task<SubscriptionSnapshot> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default) =>
        Task.FromResult(subscriptions.TryGetValue(subscriptionId, out var s) ? s : throw new PaymentGatewayException("No such subscription"));

    public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(
        string subscriptionId, PlanKey plan, BillingCycle cycle, int price, int? chargeOverride = null, CancellationToken ct = default)
    {
        if (Decline is { } why) throw new DomainException($"ชำระเงินไม่สำเร็จ: {why}");
        Changes.Enqueue((subscriptionId, plan, cycle, price, chargeOverride));
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

    // --- the in-app checkout: PaymentIntents the test plays Stripe's part for (pays, declines, processes) -------------

    /// <summary>A PaymentIntent of the in-app checkout, as Stripe would hold it.</summary>
    public sealed class FakeIntent(string id, string secret, Guid userId, decimal amount, string? subscriptionId, PaymentFlow flow)
    {
        public string Id { get; } = id;
        public string Secret { get; } = secret;
        public Guid UserId { get; } = userId;
        public decimal Amount { get; } = amount;
        public string? SubscriptionId { get; } = subscriptionId;
        public PaymentFlow Flow { get; } = flow;
        public PaymentIntentState State { get; set; } = PaymentIntentState.Pending;
        public PaymentMethodKind? Method { get; set; }
        public string? Failure { get; set; }
    }

    private readonly ConcurrentDictionary<string, FakeIntent> intents = new();

    public ConcurrentQueue<SubscriptionPaymentRequest> SubscriptionPayments { get; } = new();
    public ConcurrentQueue<PrepaidPaymentRequest> PrepaidPayments { get; } = new();
    /// <summary>How many times the API asked Stripe about an intent (a settled attempt must not be asked again).</summary>
    public int IntentReads;

    public FakeIntent Intent(string id) => intents[id];

    public string LastIntentOf(Guid userId) =>
        intents.Values.Where(i => i.UserId == userId).OrderBy(i => i.Id, StringComparer.Ordinal).Last().Id;

    public Task<StartedPayment> CreateSubscriptionPaymentAsync(SubscriptionPaymentRequest r, CancellationToken ct = default)
    {
        SubscriptionPayments.Enqueue(r);
        var sub = Next("sub");
        subscriptions[sub] = new SubscriptionSnapshot(sub, r.CustomerId, SubscriptionState.Incomplete, r.Plan, r.Cycle, null, false);
        // What Stripe asks for on the first invoice: the period's price, less the promo coupon.
        var amount = (r.ChargeOverride ?? Pricing.Period(r.Price, r.Cycle)) - r.FirstDiscount;
        return Task.FromResult(Make(r.UserId, amount, sub, PaymentFlow.Subscription));
    }

    public Task<StartedPayment> CreatePrepaidPaymentAsync(PrepaidPaymentRequest r, CancellationToken ct = default)
    {
        PrepaidPayments.Enqueue(r);
        return Task.FromResult(Make(r.UserId, r.Amount, null, PaymentFlow.Prepaid));
    }

    private StartedPayment Make(Guid userId, decimal amount, string? subscription, PaymentFlow flow)
    {
        var id = Next("pi");
        var secret = $"{id}_secret_{Guid.NewGuid():N}"[..40];
        intents[id] = new FakeIntent(id, secret, userId, amount, subscription, flow);
        return new StartedPayment(id, secret, amount, subscription);
    }

    public Task<PaymentIntentSnapshot> GetPaymentIntentAsync(string paymentIntentId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref IntentReads);
        if (!intents.TryGetValue(paymentIntentId, out var i)) throw new PaymentGatewayException("No such payment intent");
        var receipt = i.State == PaymentIntentState.Succeeded ? $"https://receipt.stripe.test/{i.Id}" : null;
        return Task.FromResult(new PaymentIntentSnapshot(i.Id, i.State, i.Amount, i.Method, i.Failure, receipt));
    }

    /// <summary>The customer pays: the intent succeeds and, for a subscription, Stripe switches it on.</summary>
    public void PayIntent(string id, PaymentMethodKind method)
    {
        var i = intents[id];
        i.State = PaymentIntentState.Succeeded;
        i.Method = method;
        i.Failure = null;
        if (i.SubscriptionId is { } sub) Set(sub, s => s with { State = SubscriptionState.Active, RenewsAt = DateTimeOffset.UtcNow.AddMonths(1) });
    }

    /// <summary>The intent succeeded but Stripe has not switched the subscription on yet.</summary>
    public void PayIntentSubscriptionLate(string id, PaymentMethodKind method)
    {
        var i = intents[id];
        i.State = PaymentIntentState.Succeeded;
        i.Method = method;
    }

    public void ActivateSubscriptionOf(string id) =>
        Set(intents[id].SubscriptionId!, s => s with { State = SubscriptionState.Active, RenewsAt = DateTimeOffset.UtcNow.AddMonths(1) });

    public void DeclineIntent(string id, string message)
    {
        var i = intents[id];
        i.State = PaymentIntentState.Failed;
        i.Failure = message;
    }

    public void ProcessIntent(string id, PaymentMethodKind method)
    {
        var i = intents[id];
        i.State = PaymentIntentState.Pending;
        i.Method = method;
        i.Failure = null;
    }

    public PaymentEvent? ParseWebhook(string payload, string? signature) => parser.Parse(payload, signature);
}
