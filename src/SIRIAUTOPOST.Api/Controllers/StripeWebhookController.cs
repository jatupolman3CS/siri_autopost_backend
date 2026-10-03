using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
public sealed class StripeWebhookController : ControllerBase
{
    /// <summary>
    /// Stripe's event delivery. Anonymous: the Stripe-Signature of the raw body is the credential. Answers 400 for
    /// a bad signature and 5xx when handling failed, so Stripe delivers the event again.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("stripe")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Stripe([FromServices] ICommandHandler<StripeWebhookCommand, Unit> handler, CancellationToken ct)
    {
        // The signature covers the exact bytes: read the body as it came, never through model binding.
        using var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(ct);
        await handler.HandleAsync(new StripeWebhookCommand(payload, Request.Headers["Stripe-Signature"].ToString()), ct);
        return Ok();
    }
}
