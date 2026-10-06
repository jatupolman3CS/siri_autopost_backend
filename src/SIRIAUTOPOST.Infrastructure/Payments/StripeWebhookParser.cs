using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Exceptions;
using Stripe;

namespace SIRIAUTOPOST.Infrastructure.Payments;

/// <summary>
/// Verifies a webhook's Stripe-Signature and reads the events the app acts on. Pure (no network): the
/// subscription behind an event is read from Stripe afterwards, so only ids and the invoice/refund facts matter here.
/// </summary>
public sealed class StripeWebhookParser
{
    private readonly string[] secrets;

    public StripeWebhookParser(string secret)
    {
        secrets = (secret ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public PaymentEvent? Parse(string payload, string? signature)
    {
        if (secrets.Length == 0) throw new InvalidWebhookException("ยังไม่ได้ตั้งค่า Stripe:WebhookSecret");
        if (string.IsNullOrWhiteSpace(signature)) throw new InvalidWebhookException("ไม่มีลายเซ็น Stripe-Signature");

        Event? e = null;
        StripeException? lastEx = null;

        foreach (var s in secrets)
        {
            try
            {
                // An endpoint created on another API version still has the fields we read; do not refuse it for the version.
                e = EventUtility.ConstructEvent(payload, signature, s, throwOnApiVersionMismatch: false);
                break;
            }
            catch (StripeException ex)
            {
                lastEx = ex;
            }
        }

        if (e is null)
        {
            throw new InvalidWebhookException($"ลายเซ็น webhook ไม่ถูกต้อง: {lastEx?.Message}");
        }

        switch (e.Type)
        {
            case EventTypes.CheckoutSessionCompleted when e.Data.Object is Stripe.Checkout.Session { Mode: "subscription" } session:
                return new CheckoutCompletedEvent(e.Id, StripeMapping.Session(session));
            case EventTypes.CustomerSubscriptionCreated or EventTypes.CustomerSubscriptionUpdated or EventTypes.CustomerSubscriptionDeleted
                when e.Data.Object is Subscription sub:
                return new SubscriptionChangedEvent(e.Id, sub.Id, sub.CustomerId);
            case EventTypes.InvoicePaid when e.Data.Object is Invoice paid:
                return new InvoicePaidEvent(e.Id, StripeMapping.Invoice(paid, paid: true));
            case EventTypes.InvoicePaymentFailed when e.Data.Object is Invoice failed:
                return new InvoiceFailedEvent(e.Id, StripeMapping.Invoice(failed, paid: false));
            case EventTypes.RefundCreated or EventTypes.RefundUpdated when e.Data.Object is Refund refund:
                return new RefundEvent(e.Id, StripeMapping.Refund(refund));
            // The in-app checkout: paid, declined, still processing (a PromptPay QR scanned) or cancelled. Only the id is
            // taken; the handler reads the PaymentIntent again, so the order the events arrive in does not matter.
            case EventTypes.PaymentIntentSucceeded or EventTypes.PaymentIntentPaymentFailed
                or EventTypes.PaymentIntentProcessing or EventTypes.PaymentIntentCanceled
                when e.Data.Object is PaymentIntent intent:
                return new PaymentIntentEvent(e.Id, intent.Id);
            default:
                return null;
        }
    }
}
