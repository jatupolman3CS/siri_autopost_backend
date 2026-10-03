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

    /// <summary>The Stripe Billing Portal address: update the card, see invoices, cancel.</summary>
    [HttpPost("billing/portal")]
    public Task<UrlDto> Portal([FromServices] ICommandHandler<CreatePortalSessionCommand, UrlDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreatePortalSessionCommand(), ct);
}
