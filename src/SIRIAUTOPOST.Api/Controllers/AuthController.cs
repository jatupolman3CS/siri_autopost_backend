using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    /// <param name="Cycle">Billing cycle; omitted keeps the current one.</param>
    public sealed record ChangePlanRequest(PlanKey Plan, BillingCycle? Cycle, string? PromoCode);

    [AllowAnonymous]
    [HttpPost("signup")]
    [ProducesResponseType<AuthResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<AuthResultDto> SignUp(SignUpCommand command, [FromServices] ICommandHandler<SignUpCommand, AuthResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(command, ct);

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<AuthResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public Task<AuthResultDto> LogIn(LogInCommand command, [FromServices] ICommandHandler<LogInCommand, AuthResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(command, ct);

    public sealed record GoogleLogInRequest(string IdToken, PlanKey? Plan);

    /// <summary>Public sign-in settings; googleClientId is null when Google sign-in is off.</summary>
    public sealed record AuthConfigDto(string? GoogleClientId);

    [AllowAnonymous]
    [HttpGet("config")]
    public AuthConfigDto Config([FromServices] IGoogleTokenVerifier google) => new(google.ClientId);

    /// <summary>Signs in (or signs up) with the ID token from Google Identity Services.</summary>
    [AllowAnonymous]
    [HttpPost("google")]
    [ProducesResponseType<AuthResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public Task<AuthResultDto> Google(GoogleLogInRequest request, [FromServices] ICommandHandler<GoogleLogInCommand, AuthResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GoogleLogInCommand(request.IdToken, request.Plan), ct);

    [HttpGet("me")]
    public Task<UserDto> Me([FromServices] IQueryHandler<GetMeQuery, UserDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetMeQuery(), ct);

    /// <summary>Changes the plan; a paid plan records a charge (no payment provider is connected yet).</summary>
    [HttpPut("me/plan")]
    public Task<UserDto> ChangePlan(ChangePlanRequest request, [FromServices] ICommandHandler<ChangePlanCommand, UserDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ChangePlanCommand(request.Plan, request.Cycle, request.PromoCode), ct);
}
