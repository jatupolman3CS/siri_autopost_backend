using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Admin;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>The platform owner's pages. Platform admins only (JWT role "admin").</summary>
[ApiController]
[Route("api/admin")]
[Produces("application/json")]
[Authorize(Roles = "admin")]
public sealed class AdminController : ControllerBase
{
    public sealed record StatusRequest(CustomerStatus Status);
    public sealed record PausedRequest(bool Paused);
    public sealed record PlanRequest(PlanKey Plan);
    public sealed record LimitsRequest(int? Accounts, int? Posts, int? Devices, int? Seats);
    public sealed record NoteRequest(string? Note);
    public sealed record PlanSettingsRequest(int Price, int? Accounts, int? Posts, int? Devices, int? Seats);
    public sealed record PromoRequest(string Code, string Discount, DateTimeOffset? ExpiresAt);
    public sealed record ActiveRequest(bool Active);

    [HttpGet("summary")]
    public Task<AdminSummaryDto> Summary([FromServices] IQueryHandler<GetAdminSummaryQuery, AdminSummaryDto> h, CancellationToken ct) =>
        h.HandleAsync(new GetAdminSummaryQuery(), ct);

    [HttpGet("health")]
    public Task<PlatformHealthDto> Health([FromServices] IQueryHandler<GetPlatformHealthQuery, PlatformHealthDto> h, CancellationToken ct) =>
        h.HandleAsync(new GetPlatformHealthQuery(), ct);

    /// <summary>The activity log, newest first (?customerId= for one customer).</summary>
    [HttpGet("audit")]
    public Task<IReadOnlyList<AuditEntryDto>> Audit(
        [FromQuery] Guid? customerId, [FromQuery] int? take,
        [FromServices] IQueryHandler<GetAuditQuery, IReadOnlyList<AuditEntryDto>> h, CancellationToken ct) =>
        h.HandleAsync(new GetAuditQuery(customerId, take ?? 50), ct);

    /// <summary>A one-hour, read-only token to see the app as the customer ("assist").</summary>
    [HttpPost("customers/{id:guid}/impersonate")]
    public Task<AuthResultDto> Impersonate(Guid id, [FromServices] ICommandHandler<ImpersonateCommand, AuthResultDto> h, CancellationToken ct) =>
        h.HandleAsync(new ImpersonateCommand(id), ct);

    [HttpGet("customers")]
    public Task<IReadOnlyList<CustomerDto>> Customers(
        [FromServices] IQueryHandler<GetCustomersQuery, IReadOnlyList<CustomerDto>> h, CancellationToken ct) =>
        h.HandleAsync(new GetCustomersQuery(), ct);

    /// <summary>Newest posts across the platform, or of one customer (?customerId=).</summary>
    [HttpGet("jobs")]
    public Task<IReadOnlyList<AdminJobDto>> Jobs(
        [FromQuery] Guid? customerId, [FromQuery] int? take,
        [FromServices] IQueryHandler<GetAdminJobsQuery, IReadOnlyList<AdminJobDto>> h, CancellationToken ct) =>
        h.HandleAsync(new GetAdminJobsQuery(customerId, take ?? 30), ct);

    /// <summary>active (restore), suspended or banned. Suspended and banned customers cannot sign in or post.</summary>
    [HttpPost("customers/{id:guid}/status")]
    public Task<CustomerDto> Status(Guid id, StatusRequest r, [FromServices] ICommandHandler<SetCustomerStatusCommand, CustomerDto> h, CancellationToken ct) =>
        h.HandleAsync(new SetCustomerStatusCommand(id, r.Status), ct);

    [HttpPost("customers/{id:guid}/pause")]
    public Task<CustomerDto> Pause(Guid id, PausedRequest r, [FromServices] ICommandHandler<SetCustomerPausedCommand, CustomerDto> h, CancellationToken ct) =>
        h.HandleAsync(new SetCustomerPausedCommand(id, r.Paused), ct);

    [HttpPut("customers/{id:guid}/plan")]
    public Task<CustomerDto> Plan(Guid id, PlanRequest r, [FromServices] ICommandHandler<SetCustomerPlanCommand, CustomerDto> h, CancellationToken ct) =>
        h.HandleAsync(new SetCustomerPlanCommand(id, r.Plan), ct);

    /// <summary>null keeps the plan's value, 0 = unlimited.</summary>
    [HttpPut("customers/{id:guid}/limits")]
    public Task<CustomerDto> Limits(Guid id, LimitsRequest r, [FromServices] ICommandHandler<SetCustomerLimitsCommand, CustomerDto> h, CancellationToken ct) =>
        h.HandleAsync(new SetCustomerLimitsCommand(id, r.Accounts, r.Posts, r.Devices, r.Seats), ct);

    [HttpPut("customers/{id:guid}/note")]
    public Task<CustomerDto> Note(Guid id, NoteRequest r, [FromServices] ICommandHandler<SetCustomerNoteCommand, CustomerDto> h, CancellationToken ct) =>
        h.HandleAsync(new SetCustomerNoteCommand(id, r.Note), ct);

    [HttpDelete("customers/{id:guid}/devices/{deviceId:guid}")]
    public Task<CustomerDto> RevokeDevice(
        Guid id, Guid deviceId, [FromServices] ICommandHandler<AdminRevokeDeviceCommand, CustomerDto> h, CancellationToken ct) =>
        h.HandleAsync(new AdminRevokeDeviceCommand(id, deviceId), ct);

    /// <summary>Puts the customer's failed posts back in the queue; returns how many.</summary>
    [HttpPost("customers/{id:guid}/retry-failed")]
    public Task<int> RetryFailed(Guid id, [FromServices] ICommandHandler<RetryCustomerFailedCommand, int> h, CancellationToken ct) =>
        h.HandleAsync(new RetryCustomerFailedCommand(id), ct);

    /// <summary>Refunds the customer's latest charge.</summary>
    [HttpPost("customers/{id:guid}/refund")]
    public Task<TransactionDto> RefundLatest(Guid id, [FromServices] ICommandHandler<RefundCommand, TransactionDto> h, CancellationToken ct) =>
        h.HandleAsync(new RefundCommand(null, id), ct);

    [HttpGet("transactions")]
    public Task<IReadOnlyList<TransactionDto>> Transactions(
        [FromServices] IQueryHandler<GetTransactionsQuery, IReadOnlyList<TransactionDto>> h, CancellationToken ct) =>
        h.HandleAsync(new GetTransactionsQuery(), ct);

    [HttpPost("transactions/{id:guid}/refund")]
    public Task<TransactionDto> Refund(Guid id, [FromServices] ICommandHandler<RefundCommand, TransactionDto> h, CancellationToken ct) =>
        h.HandleAsync(new RefundCommand(id, null), ct);

    /// <summary>Records that a failed charge was paid (no payment provider is connected).</summary>
    [HttpPost("transactions/{id:guid}/paid")]
    public Task<TransactionDto> Paid(Guid id, [FromServices] ICommandHandler<RecordPaymentCommand, TransactionDto> h, CancellationToken ct) =>
        h.HandleAsync(new RecordPaymentCommand(id), ct);

    [HttpPut("plans/{key}")]
    public Task<PlanDto> UpdatePlan(PlanKey key, PlanSettingsRequest r, [FromServices] ICommandHandler<UpdatePlanCommand, PlanDto> h, CancellationToken ct) =>
        h.HandleAsync(new UpdatePlanCommand(key, r.Price, r.Accounts, r.Posts, r.Devices, r.Seats), ct);

    [HttpGet("promos")]
    public Task<IReadOnlyList<PromoDto>> Promos([FromServices] IQueryHandler<GetPromosQuery, IReadOnlyList<PromoDto>> h, CancellationToken ct) =>
        h.HandleAsync(new GetPromosQuery(), ct);

    [HttpPost("promos")]
    public Task<PromoDto> CreatePromo(PromoRequest r, [FromServices] ICommandHandler<CreatePromoCommand, PromoDto> h, CancellationToken ct) =>
        h.HandleAsync(new CreatePromoCommand(r.Code, r.Discount, r.ExpiresAt), ct);

    [HttpPut("promos/{code}/active")]
    public Task<PromoDto> PromoActive(string code, ActiveRequest r, [FromServices] ICommandHandler<SetPromoActiveCommand, PromoDto> h, CancellationToken ct) =>
        h.HandleAsync(new SetPromoActiveCommand(code, r.Active), ct);
}
