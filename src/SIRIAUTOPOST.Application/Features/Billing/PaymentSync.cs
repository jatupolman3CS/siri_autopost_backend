using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Billing;

/// <summary>
/// Turns what Stripe says (a subscription, an invoice, a refund, a finished Checkout) into the customer's plan,
/// status and ledger. Stripe is the source of truth: the web app never moves a paid plan by itself, and
/// everything here is safe to run twice. Callers save through their own unit of work.
/// </summary>
public sealed class PaymentSync(
    IUserRepository users, ITransactionRepository transactions, IPromoRepository promos, IPaymentEventRepository processed,
    IAuditRepository audit, IPaymentGateway gateway, TimeProvider clock)
{
    /// <summary>The actor of changes Stripe made on its own (a renewal that failed for good, a refund in its dashboard).</summary>
    public static readonly Guid System = Guid.Empty;

    /// <summary>
    /// Applies a subscription to its customer. A live subscription sets the plan; a finished one sends the customer
    /// back to Free, but only when it is the subscription they are on (an older one ending must not undo a new one).
    /// </summary>
    public void ApplySubscription(User user, SubscriptionSnapshot s, Guid actor)
    {
        var from = user.Plan;
        switch (s.State)
        {
            case SubscriptionState.Active or SubscriptionState.PastDue:
                if (s.Plan is not { } plan || s.Cycle is not { } cycle) return; // not a subscription of ours
                user.ApplySubscription(s.Id, plan, cycle, s.RenewsAt, s.CancelAtPeriodEnd, s.State == SubscriptionState.PastDue);
                break;
            case SubscriptionState.Canceled:
                if (user.StripeSubscriptionId != s.Id) return;
                user.EndSubscription();
                break;
            default:
                return; // incomplete: the first payment has not gone through
        }
        if (from != user.Plan) audit.Add(AuditEntry.PlanChange(actor, user, from, clock.GetUtcNow()));
    }

    /// <summary>A paid Checkout session: links the Stripe customer, starts the subscription it created and counts the promo code.</summary>
    public async Task CompleteCheckoutAsync(CheckoutSessionSnapshot session, Guid actor, CancellationToken ct)
    {
        var key = $"checkout:{session.Id}";
        if (await processed.ExistsAsync(key, ct)) return;
        var user = session.UserId is { } id ? await users.GetByIdAsync(id, ct) : null;
        user ??= session.CustomerId is { } customer ? await users.GetByStripeCustomerAsync(customer, ct) : null;
        if (user is null) return; // a session of something else

        if (session.CustomerId is not null && user.StripeCustomerId is null) user.LinkStripeCustomer(session.CustomerId);
        if (session.SubscriptionId is { } subscription)
            ApplySubscription(user, await gateway.GetSubscriptionAsync(subscription, ct), actor);
        if (session.PromoCode is { } code && await promos.GetByCodeAsync(Promo.NormalizeCode(code), ct) is { } promo) promo.RecordUse();
        processed.Add(ProcessedPaymentEvent.Create(key, "checkout", clock.GetUtcNow()));
    }

    /// <summary>
    /// A paid invoice becomes a charge in the ledger (a failed row of the same invoice becomes paid). Invoices of
    /// 0 baht (a free first month) moved no money and are not recorded.
    /// </summary>
    public async Task RecordInvoicePaidAsync(InvoiceSnapshot invoice, CancellationToken ct)
    {
        if (invoice.Amount <= 0) return;
        var user = await users.GetByStripeCustomerAsync(invoice.CustomerId, ct);
        if (user is null) return;
        var row = await transactions.GetByInvoiceAsync(invoice.Id, ct);
        if (row is { Type: not TransactionType.Failed }) return; // already recorded

        var now = clock.GetUtcNow();
        var refs = new PaymentRefs(invoice.Id, await gateway.GetInvoicePaymentIntentAsync(invoice.Id, ct), invoice.ReceiptUrl);
        if (row is null)
            transactions.Add(Transaction.Charge(user.Id, invoice.Amount, invoice.Plan ?? user.Plan, invoice.Cycle ?? user.Cycle, invoice.PromoCode, now, refs));
        else row.MarkPaid(now, invoice.Amount, refs);
        if (user.StripeSubscriptionId == invoice.SubscriptionId) user.MarkPaid();
    }

    /// <summary>
    /// A renewal that could not be collected: a failed row in the ledger and the customer is past due while Stripe
    /// retries. The first invoice of a new subscription is skipped: its Checkout page shows the failure and lets
    /// the customer try another card.
    /// </summary>
    public async Task RecordInvoiceFailedAsync(InvoiceSnapshot invoice, CancellationToken ct)
    {
        if (invoice.IsFirst) return;
        var user = await users.GetByStripeCustomerAsync(invoice.CustomerId, ct);
        if (user is null || await transactions.GetByInvoiceAsync(invoice.Id, ct) is not null) return;
        transactions.Add(Transaction.FailedCharge(
            user.Id, invoice.Amount, invoice.Plan ?? user.Plan, invoice.Cycle ?? user.Cycle, clock.GetUtcNow(),
            new PaymentRefs(invoice.Id, null, invoice.ReceiptUrl)));
        if (user.StripeSubscriptionId == invoice.SubscriptionId) user.MarkPastDue();
    }

    /// <summary>A refund Stripe made (from the dashboard, or an earlier delivery of the admin's own) becomes a refund row once.</summary>
    public async Task RecordRefundAsync(RefundSnapshot refund, CancellationToken ct)
    {
        if (!refund.Succeeded || refund.PaymentIntentId is null) return;
        if (await transactions.GetByRefundAsync(refund.Id, ct) is not null) return;
        var charge = await transactions.GetChargeByPaymentIntentAsync(refund.PaymentIntentId, ct);
        if (charge is null) return;
        var now = clock.GetUtcNow();
        var row = charge.RefundOf(now, Math.Min(refund.Amount, charge.Amount), refund.Id);
        transactions.Add(row);
        audit.Add(AuditEntry.Create(System, charge.UserId, AuditAction.Refunded, now, charge.Id.ToString(), Money.Text(row.Amount)));
    }
}
