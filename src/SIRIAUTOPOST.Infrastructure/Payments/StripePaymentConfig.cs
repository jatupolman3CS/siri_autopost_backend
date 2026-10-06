using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Payments;

/// <summary>What the browser may know about Stripe: the publishable key and the currency, never the secret key.</summary>
public sealed class StripePaymentConfig(IOptions<StripeOptions> options) : IPaymentConfig
{
    private readonly StripeOptions o = options.Value;

    // A publishable key without a secret key would start a checkout the server cannot finish: hand out neither.
    public string? PublishableKey =>
        string.IsNullOrWhiteSpace(o.SecretKey) || string.IsNullOrWhiteSpace(o.PublishableKey) ? null : o.PublishableKey.Trim();

    public string Currency => string.IsNullOrWhiteSpace(o.Currency) ? "thb" : o.Currency.Trim().ToLowerInvariant();
}
