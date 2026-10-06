using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.DTOs;

/// <param name="PublishableKey">pk_...: what Stripe.js is started with; null = no in-app checkout, the web app sends the customer to Stripe's page.</param>
public sealed record PaymentConfigDto(string? PublishableKey, string Currency);

/// <param name="Id">The Stripe PaymentIntent (pi_...); the status call is made with it.</param>
/// <param name="ClientSecret">What the browser confirms the payment with (Stripe.js); only the paying customer gets it.</param>
/// <param name="Amount">Baht to pay now: the plan's price for one period, promo code or test amount included.</param>
/// <param name="Flow">subscription: the plan renews by itself. prepaid: one period, no renewal (PromptPay).</param>
public sealed record PaymentIntentDto(string Id, string ClientSecret, decimal Amount, string Currency, PaymentFlow Flow, PaymentMethodKind Method);

/// <param name="Status">pending, succeeded or failed: the server's own record, updated by Stripe's webhook and by this call.</param>
/// <param name="Method">How it was really paid; null until a payment method has been used.</param>
/// <param name="User">The customer as it now stands: the new plan once the payment succeeded.</param>
public sealed record PaymentStatusDto(
    string Id, PaymentAttemptState Status, PaymentMethodKind? Method, string? FailureMessage, decimal Amount, PaymentFlow Flow, UserDto User);
