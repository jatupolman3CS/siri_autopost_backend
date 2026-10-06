namespace SIRIAUTOPOST.Infrastructure.Payments;

/// <summary>The "Stripe" configuration section (env: Stripe__SecretKey, Stripe__WebhookSecret ...).</summary>
public sealed class StripeOptions
{
    public const string Section = "Stripe";

    /// <summary>Secret API key (sk_test_... / sk_live_...). Empty switches payments off: paid plans cannot be bought.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// Publishable key (pk_test_... / pk_live_...) of the same Stripe account: the browser starts Stripe.js with it
    /// for the in-app payment window. Empty: the web app sends customers to Stripe's hosted Checkout page instead.
    /// </summary>
    public string PublishableKey { get; set; } = "";

    /// <summary>
    /// Payment method types the in-app checkout offers for a subscription (card, Apple Pay, Google Pay and Link), comma
    /// separated. Apple Pay and Google Pay are card wallets, so they need no entry. Take <c>link</c> out when the Stripe
    /// account cannot use Link; empty leaves the choice to the account's Stripe Dashboard settings.
    /// </summary>
    public string SubscriptionPaymentMethods { get; set; } = "card,link";

    /// <summary>Signing secret (whsec_...) of the webhook endpoint. Multiple comma-separated secrets supported (e.g. for key rotation).</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>
    /// Address of the web app Stripe sends customers back to. Empty: the origin of the browser request,
    /// which is right whenever the web app and the API share one site (the nginx proxy, Caddy).
    /// </summary>
    public string ReturnBaseUrl { get; set; } = "";

    /// <summary>ISO currency of every price. Plan prices are whole units of it (baht).</summary>
    public string Currency { get; set; } = "thb";

    /// <summary>A Billing Portal configuration (bpc_...) to use instead of the account's default.</summary>
    public string? PortalConfigurationId { get; set; }
}
