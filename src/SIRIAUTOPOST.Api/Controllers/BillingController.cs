using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api")]
[Produces("application/json")]
public sealed class BillingController : ControllerBase
{
    public sealed record NotificationsRequest(bool NotifyFailed, bool NotifyExpiring, bool NotifyRenewal);
    /// <summary>What the payment provider returns for a tokenized card. The number and the CVC never come here.</summary>
    public sealed record PaymentMethodRequest(string? Brand, string Last4, int ExpMonth, int ExpYear);

    /// <summary>Prices and limits of every plan (public: the landing page shows them).</summary>
    [AllowAnonymous]
    [HttpGet("plans")]
    public Task<IReadOnlyList<PlanDto>> Plans([FromServices] IQueryHandler<GetPlansQuery, IReadOnlyList<PlanDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetPlansQuery(), ct);

    /// <summary>The signed-in customer's charges and refunds, newest first.</summary>
    [HttpGet("billing/invoices")]
    public Task<IReadOnlyList<TransactionDto>> Invoices(
        [FromServices] IQueryHandler<GetInvoicesQuery, IReadOnlyList<TransactionDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetInvoicesQuery(), ct);

    /// <summary>A printable statement (HTML) of one of the customer's own charges or refunds.</summary>
    [HttpGet("billing/invoices/{id:guid}/statement")]
    [Produces("text/html")]
    public async Task<ContentResult> Statement(
        Guid id, [FromServices] IQueryHandler<GetStatementQuery, StatementDto> handler, CancellationToken ct)
    {
        var statement = await handler.HandleAsync(new GetStatementQuery(id), ct);
        return Content(StatementHtml.Render(statement), "text/html; charset=utf-8");
    }

    /// <summary>Notification preferences and the card on file.</summary>
    [HttpGet("billing/profile")]
    public Task<BillingProfileDto> Profile([FromServices] IQueryHandler<GetBillingProfileQuery, BillingProfileDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetBillingProfileQuery(), ct);

    [HttpPut("billing/profile/notifications")]
    public Task<BillingProfileDto> Notifications(
        NotificationsRequest r, [FromServices] ICommandHandler<UpdateBillingNotificationsCommand, BillingProfileDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateBillingNotificationsCommand(r.NotifyFailed, r.NotifyExpiring, r.NotifyRenewal), ct);

    [HttpPut("billing/profile/payment-method")]
    public Task<BillingProfileDto> SetPaymentMethod(
        PaymentMethodRequest r, [FromServices] ICommandHandler<SetPaymentMethodCommand, BillingProfileDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetPaymentMethodCommand(r.Brand, r.Last4, r.ExpMonth, r.ExpYear), ct);

    [HttpDelete("billing/profile/payment-method")]
    public Task<BillingProfileDto> RemovePaymentMethod(
        [FromServices] ICommandHandler<RemovePaymentMethodCommand, BillingProfileDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new RemovePaymentMethodCommand(), ct);
}
