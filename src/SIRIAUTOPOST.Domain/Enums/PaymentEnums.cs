namespace SIRIAUTOPOST.Domain.Enums;

/// <summary>
/// How a customer pays in the in-app checkout. Card, Apple Pay, Google Pay and Link all pay a Stripe subscription;
/// PromptPay is single-use at Stripe (it cannot be charged again by itself), so it pays one period of a plan up front.
/// The JSON names are the snake_case of the members (<c>promptpay</c>, one word, like Stripe's own name for it).
/// </summary>
public enum PaymentMethodKind
{
    Card,
    ApplePay,
    GooglePay,
    Link,
    Promptpay,
}

/// <summary>Subscription: the plan renews by itself. Prepaid: one payment buys one period, then the plan ends.</summary>
public enum PaymentFlow
{
    Subscription,
    Prepaid,
}

/// <summary>Where one payment attempt stands, as the last thing Stripe told us. Succeeded is final.</summary>
public enum PaymentAttemptState
{
    Pending,
    Succeeded,
    Failed,
}
