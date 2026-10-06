using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SIRIAUTOPOST.Api.IntegrationTests.Payments;

/// <summary>Stripe webhook bodies in the shape Stripe sends them, and the Stripe-Signature header for them.</summary>
public static class StripeEvents
{
    private static int counter;

    public static string NewId() => $"evt_{Interlocked.Increment(ref counter):D6}{Guid.NewGuid():N}"[..20];

    public static string Event(string type, object data, string? id = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["id"] = id ?? NewId(),
        ["object"] = "event",
        ["api_version"] = "2025-09-30.clover", // an older version than the SDK's: the parser must not refuse it
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["livemode"] = false,
        ["pending_webhooks"] = 1,
        ["type"] = type,
        ["data"] = new Dictionary<string, object?> { ["object"] = data },
    });

    public static Dictionary<string, object?> Metadata(Guid userId, string plan, string cycle, string? promo = null)
    {
        var m = new Dictionary<string, object?> { ["user_id"] = userId.ToString(), ["plan"] = plan, ["cycle"] = cycle };
        if (promo is not null) m["promo"] = promo;
        return m;
    }

    public static object Session(
        string id, string customer, string? subscription, Guid userId, string plan, string cycle, string? promo = null, string mode = "subscription",
        string? paymentIntent = null, decimal baht = 0) =>
        new Dictionary<string, object?>
        {
            ["id"] = id, ["object"] = "checkout.session", ["mode"] = mode, ["status"] = "complete", ["payment_status"] = "paid",
            ["customer"] = customer, ["subscription"] = subscription, ["client_reference_id"] = userId.ToString(),
            ["payment_intent"] = paymentIntent, ["amount_total"] = (long)(baht * 100),
            ["metadata"] = Metadata(userId, plan, cycle, promo),
        };

    public static object Subscription(string id, string customer, string status = "active") => new Dictionary<string, object?>
    {
        ["id"] = id, ["object"] = "subscription", ["customer"] = customer, ["status"] = status,
        ["items"] = new Dictionary<string, object?> { ["object"] = "list", ["data"] = Array.Empty<object>(), ["has_more"] = false, ["url"] = "/v1/subscription_items" },
    };

    /// <param name="baht">Charged (invoice.paid) or asked for (invoice.payment_failed), in baht.</param>
    public static object Invoice(
        string id, string customer, string subscription, decimal baht, Guid userId, string plan, string cycle,
        string reason = "subscription_cycle", string? promo = null) =>
        new Dictionary<string, object?>
        {
            ["id"] = id, ["object"] = "invoice", ["customer"] = customer, ["currency"] = "thb",
            ["amount_paid"] = (long)(baht * 100), ["amount_due"] = (long)(baht * 100),
            ["billing_reason"] = reason, ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["hosted_invoice_url"] = $"https://invoice.stripe.test/i/{id}",
            ["parent"] = new Dictionary<string, object?>
            {
                ["type"] = "subscription_details",
                ["subscription_details"] = new Dictionary<string, object?>
                {
                    ["subscription"] = subscription, ["metadata"] = Metadata(userId, plan, cycle, promo),
                },
            },
        };

    /// <summary>A PaymentIntent as payment_intent.* events carry it (the app only takes its id and reads the rest from Stripe).</summary>
    public static object PaymentIntent(string id, string status = "succeeded", decimal baht = 790) => new Dictionary<string, object?>
    {
        ["id"] = id, ["object"] = "payment_intent", ["status"] = status, ["amount"] = (long)(baht * 100), ["currency"] = "thb",
    };

    public static object Refund(string id, string paymentIntent, decimal baht, string status = "succeeded") => new Dictionary<string, object?>
    {
        ["id"] = id, ["object"] = "refund", ["amount"] = (long)(baht * 100), ["currency"] = "thb",
        ["payment_intent"] = paymentIntent, ["status"] = status, ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    /// <summary>The Stripe-Signature header: <c>t=...,v1=HMAC-SHA256(secret, "t.payload")</c>.</summary>
    public static string Signature(string payload, string secret, DateTimeOffset? at = null)
    {
        var t = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}"));
        return $"t={t},v1={Convert.ToHexStringLower(hash)}";
    }
}
