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
}
