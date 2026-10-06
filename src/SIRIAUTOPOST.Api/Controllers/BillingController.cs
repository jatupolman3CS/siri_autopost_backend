using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api")]
[Produces("application/json")]
public sealed class BillingController : ControllerBase
{
    /// <param name="Cycle">Billing cycle; omitted keeps the current one.</param>
    /// <param name="PromoCode">Applies to the first invoice of a new subscription only.</param>
    public sealed record ChangePlanRequest(PlanKey Plan, BillingCycle? Cycle, string? PromoCode);

    /// <param name="SessionId">The id Stripe put in the return address (cs_...).</param>
    public sealed record ConfirmCheckoutRequest(string SessionId);

    /// <summary>Prices and limits of every plan (public: the landing page shows them).</summary>
    [AllowAnonymous]
    [HttpGet("plans")]
    public Task<IReadOnlyList<PlanDto>> Plans([FromServices] IQueryHandler<GetPlansQuery, IReadOnlyList<PlanDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetPlansQuery(), ct);

    /// <summary>The signed-in customer's plan, renewal date and card.</summary>
    [HttpGet("billing")]
    public Task<BillingDto> Billing([FromServices] IQueryHandler<GetBillingQuery, BillingDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetBillingQuery(), ct);

    /// <summary>The signed-in customer's charges and refunds, newest first.</summary>
    [HttpGet("billing/invoices")]
    public Task<IReadOnlyList<TransactionDto>> Invoices(
        [FromServices] IQueryHandler<GetInvoicesQuery, IReadOnlyList<TransactionDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetInvoicesQuery(), ct);

    /// <summary>
    /// Chooses a plan. A paid plan for a customer with no subscription answers with the Stripe Checkout address
    /// (<c>checkoutUrl</c>) to send the customer to; otherwise the plan changes at once (or, for Free, at the end
    /// of the paid period).
    /// </summary>
    [HttpPut("billing/plan")]
    [ProducesResponseType<PlanChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<PlanChangeDto> ChangePlan(
        ChangePlanRequest request, [FromServices] ICommandHandler<ChangePlanCommand, PlanChangeDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ChangePlanCommand(request.Plan, request.Cycle, request.PromoCode), ct);

    /// <summary>The customer is back from Stripe Checkout: applies the paid session (the webhook does the same).</summary>
    [HttpPost("billing/checkout/confirm")]
    public Task<UserDto> ConfirmCheckout(
        ConfirmCheckoutRequest request, [FromServices] ICommandHandler<ConfirmCheckoutCommand, UserDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ConfirmCheckoutCommand(request.SessionId), ct);

    /// <summary>The publishable key the in-app payment window starts Stripe.js with (null key: use Stripe's own page).</summary>
    [HttpGet("billing/payment-config")]
    public Task<PaymentConfigDto> PaymentConfig(
        [FromServices] IQueryHandler<GetPaymentConfigQuery, PaymentConfigDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetPaymentConfigQuery(), ct);

    /// <param name="Method">The row picked in the payment window; promptpay pays one period up front, the others a subscription.</param>
    /// <param name="PromoCode">Takes its discount off this first payment.</param>
    public sealed record StartPaymentRequest(PlanKey Plan, BillingCycle? Cycle, string? PromoCode, PaymentMethodKind Method);

    /// <summary>
    /// Starts paying for a plan in the in-app window: answers with the Stripe PaymentIntent's client secret, which the
    /// browser confirms with Stripe.js. Nothing changes until Stripe says the payment went through.
    /// </summary>
    [HttpPost("billing/payments")]
    [ProducesResponseType<PaymentIntentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<PaymentIntentDto> StartPayment(
        StartPaymentRequest request, [FromServices] ICommandHandler<StartPaymentCommand, PaymentIntentDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new StartPaymentCommand(request.Plan, request.Cycle, request.PromoCode, request.Method), ct);

    /// <summary>
    /// Stripe.js finished, or the window asks again while a PromptPay payment is pending: reads the PaymentIntent from
    /// Stripe, applies it (the webhook does the same) and answers pending, succeeded or failed with the customer's new plan.
    /// </summary>
    [HttpPost("billing/payments/{id}/confirm")]
    public Task<PaymentStatusDto> ConfirmPayment(
        string id, [FromServices] ICommandHandler<ConfirmPaymentCommand, PaymentStatusDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ConfirmPaymentCommand(id), ct);

    /// <summary>The Stripe Billing Portal address: update the card, see invoices, cancel.</summary>
    [HttpPost("billing/portal")]
    public Task<UrlDto> Portal([FromServices] ICommandHandler<CreatePortalSessionCommand, UrlDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreatePortalSessionCommand(), ct);
}
