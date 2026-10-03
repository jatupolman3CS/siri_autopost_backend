using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Features.Admin;

// The platform owner's view of every customer. The controller only lets platform admins in.
// Job figures count posts of accounts connected through the extension (not the sample accounts).

/// <summary>Every customer (users that are not platform admins), newest first.</summary>
public sealed record GetCustomersQuery : IQuery<IReadOnlyList<CustomerDto>>;

public sealed class GetCustomersQueryHandler(AdminCustomers customers)
    : IQueryHandler<GetCustomersQuery, IReadOnlyList<CustomerDto>>
{
    public Task<IReadOnlyList<CustomerDto>> HandleAsync(GetCustomersQuery q, CancellationToken ct = default) =>
        customers.ListAsync(null, ct);
}

/// <summary>Subscriptions per paid plan and net revenue of the last 12 months (Thai calendar).</summary>
public sealed record GetAdminSummaryQuery : IQuery<AdminSummaryDto>;

public sealed class GetAdminSummaryQueryHandler(IUserRepository users, ITransactionRepository transactions, TimeProvider clock)
    : IQueryHandler<GetAdminSummaryQuery, AdminSummaryDto>
{
    private static readonly TimeSpan Thai = TimeSpan.FromHours(7);

    public async Task<AdminSummaryDto> HandleAsync(GetAdminSummaryQuery q, CancellationToken ct = default)
    {
        var paying = (await users.ListAsync(ct))
            .Where(u => u.Role == UserRole.User && u.Plan != PlanKey.Free && u.Status is CustomerStatus.Active or CustomerStatus.PastDue)
            .ToList();
        var now = clock.GetUtcNow().ToOffset(Thai);
        var first = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, Thai).AddMonths(-11);
        var tx = await transactions.ListAsync(first.ToUniversalTime(), ct); // Npgsql takes UTC only
        var months = Enumerable.Range(0, 12).Select(i => first.AddMonths(i)).Select(m =>
        {
            var inMonth = tx.Where(t => t.CreatedAt.ToOffset(Thai) is var d && d.Year == m.Year && d.Month == m.Month);
            var net = inMonth.Sum(t => t.Type == TransactionType.Charge ? t.Amount : t.Type == TransactionType.Refund ? -t.Amount : 0m);
            return new RevenueMonthDto(m.Year, m.Month, net);
        }).ToList();
        return new AdminSummaryDto(
            paying.Count(u => u.Plan == PlanKey.Basic), paying.Count(u => u.Plan == PlanKey.Pro), paying.Count(u => u.Plan == PlanKey.Agency),
            months);
    }
}

/// <summary>Live platform figures for the admin overview (see <see cref="PlatformHealthDto"/>).</summary>
public sealed record GetPlatformHealthQuery : IQuery<PlatformHealthDto>;

public sealed class GetPlatformHealthQueryHandler(
    IUserRepository users, IAuditRepository audit, IPlanRepository plans, IWorkspaceRepository workspaces,
    IDeviceRepository devices, IPostRepository posts, IRequestTimings timings, IDatabaseProbe database, IDeviceEventBus events,
    IPaymentGateway gateway, TimeProvider clock)
    : IQueryHandler<GetPlatformHealthQuery, PlatformHealthDto>
{
    public async Task<PlatformHealthDto> HandleAsync(GetPlatformHealthQuery q, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var month = TimeSpan.FromDays(30);
        var customers = (await users.ListAsync(ct)).Where(u => u.Role == UserRole.User).ToList();
        var changes = await audit.ListPlanChangesAsync(now - 2 * month, ct);
        var prices = (await plans.ListAsync(ct)).ToDictionary(p => p.Key, p => p.Price);

        var wsIds = (await workspaces.ListAllAsync(ct)).Select(w => w.Id).ToList();
        var devs = await devices.ListByWorkspacesAsync(wsIds, ct);
        var latest = devs.Select(d => d.Version).Where(v => Version.TryParse(v, out _)).MaxBy(Version.Parse);

        // Finished posts per window: success and "pending approval" went out, failed did not.
        async Task<(int Ok, int Failed)> Finished(DateTimeOffset from, DateTimeOffset to)
        {
            var rows = await posts.CountByStatusAsync(wsIds, from, to, ct);
            return (rows.Where(r => r.Status is PostStatus.Success or PostStatus.Pending).Sum(r => r.Count),
                rows.Where(r => r.Status == PostStatus.Failed).Sum(r => r.Count));
        }
        var week = await Finished(now.AddDays(-7), now);
        var weekBefore = await Finished(now.AddDays(-14), now.AddDays(-7));
        var day = await Finished(now.AddDays(-1), now);
        var due = (await posts.CountByStatusAsync(wsIds, DateTimeOffset.UnixEpoch, now, ct)).Where(r => r.Status == PostStatus.Queued).Sum(r => r.Count);
        var next = (await posts.CountByStatusAsync(wsIds, now, now.AddDays(1), ct)).Where(r => r.Status == PostStatus.Queued).Sum(r => r.Count);

        var (p95, samples) = timings.Snapshot();
        var db = await database.PingAsync(ct);
        return new PlatformHealthDto(
            PlatformMetrics.Mrr(customers, changes, prices, now), PlatformMetrics.Mrr(customers, changes, prices, now - month),
            PlatformMetrics.Churn(customers, changes, now - month, now, now),
            PlatformMetrics.Churn(customers, changes, now - 2 * month, now - month, now),
            devs.Count(d => d.LastSeenAt >= now.AddDays(-1)), devs.Count,
            PlatformMetrics.SuccessRate(week.Ok, week.Failed), PlatformMetrics.SuccessRate(weekBefore.Ok, weekBefore.Failed),
            p95, samples, (int)Math.Ceiling(db.TotalMilliseconds),
            due, next,
            latest, latest is null ? 0 : devs.Count(d => d.Version == latest),
            day.Ok + day.Failed == 0 ? null : Math.Round(100.0 * day.Failed / (day.Ok + day.Failed), 1),
            PaymentsConnected: gateway.Enabled,
            events.Stats.Streams, events.Stats.DeviceWaits, events.Stats.Published, events.Stats.Dropped);
    }
}

/// <summary>Newest posts across the platform (or of one customer), for the jobs page.</summary>
public sealed record GetAdminJobsQuery(Guid? CustomerId, int Take) : IQuery<IReadOnlyList<AdminJobDto>>;

public sealed class GetAdminJobsQueryHandler(
    IUserRepository users, IWorkspaceRepository workspaces, IPostRepository posts, TimeProvider clock)
    : IQueryHandler<GetAdminJobsQuery, IReadOnlyList<AdminJobDto>>
{
    public async Task<IReadOnlyList<AdminJobDto>> HandleAsync(GetAdminJobsQuery q, CancellationToken ct = default)
    {
        var all = await workspaces.ListAllAsync(ct);
        var scope = q.CustomerId is { } id ? all.Where(w => w.OwnerId == id).ToList() : all.ToList();
        var owners = (await users.ListByIdsAsync(scope.Select(w => w.OwnerId).Distinct(), ct)).ToDictionary(u => u.Id);
        var ownerOf = scope.ToDictionary(w => w.Id, w => owners[w.OwnerId]);
        // Up to an hour ahead, so the queue that is about to run shows too.
        var list = await posts.ListRecentAsync(scope.Select(w => w.Id), clock.GetUtcNow().AddHours(1), Math.Clamp(q.Take, 1, 200), ct);
        return list.Select(p => new AdminJobDto(
                p.Id, ownerOf[p.WorkspaceId].Id, ownerOf[p.WorkspaceId].Name, p.Platform, p.Target, p.Content, p.ScheduledAt, p.Status,
                p.FailureCode))
            .ToList();
    }
}

/// <summary>
/// Sets the customer's standing. A customer who pays through Stripe is not billed while blocked: collection
/// pauses when they are suspended or banned and resumes when they are restored.
/// </summary>
public sealed record SetCustomerStatusCommand(Guid CustomerId, CustomerStatus Status) : ICommand<CustomerDto>;

public sealed class SetCustomerStatusCommandHandler(
    IUserRepository users, AdminCustomers customers, AdminAudit audit, IPaymentGateway gateway, IUnitOfWork uow)
    : ICommandHandler<SetCustomerStatusCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(SetCustomerStatusCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        var from = user.Status;
        var wasBlocked = user.IsBlocked;
        user.SetStatus(c.Status);
        // Before the save: when Stripe cannot be reached the status stays as it was, so nobody is billed while blocked.
        if (user.StripeSubscriptionId is { } subscription && wasBlocked != user.IsBlocked)
            await gateway.SetBillingPausedAsync(subscription, user.IsBlocked, ct);
        audit.Record(AuditAction.StatusChanged, user.Id, AdminAudit.Key(from), AdminAudit.Key(c.Status));
        await uow.SaveChangesAsync(ct);
        return await customers.GetAsync(user.Id, ct);
    }
}

public sealed record SetCustomerPausedCommand(Guid CustomerId, bool Paused) : ICommand<CustomerDto>;

public sealed class SetCustomerPausedCommandHandler(IUserRepository users, AdminCustomers customers, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<SetCustomerPausedCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(SetCustomerPausedCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        if (!c.Paused && user.IsBlocked) throw new DomainException("ลูกค้าถูกระงับอยู่ คืนสถานะก่อน");
        user.SetPaused(c.Paused);
        audit.Record(AuditAction.PauseChanged, user.Id, to: c.Paused ? "paused" : "running");
        await uow.SaveChangesAsync(ct);
        return await customers.GetAsync(user.Id, ct);
    }
}

/// <summary>
/// Grants the customer another plan without a charge, and clears their limit overrides. Refused while the
/// customer pays through a Stripe subscription (they change plan themselves).
/// </summary>
public sealed record SetCustomerPlanCommand(Guid CustomerId, PlanKey Plan) : ICommand<CustomerDto>;

public sealed class SetCustomerPlanCommandHandler(IUserRepository users, AdminCustomers customers, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<SetCustomerPlanCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(SetCustomerPlanCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        var from = user.Plan;
        user.SetPlanByAdmin(c.Plan);
        if (from != c.Plan) audit.PlanChange(user, from);
        await uow.SaveChangesAsync(ct);
        return await customers.GetAsync(user.Id, ct);
    }
}

/// <summary>Per-customer limits: null keeps the plan's value, 0 = unlimited.</summary>
public sealed record SetCustomerLimitsCommand(Guid CustomerId, int? Accounts, int? Posts, int? Devices, int? Seats) : ICommand<CustomerDto>;

public sealed class SetCustomerLimitsCommandHandler(IUserRepository users, AdminCustomers customers, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<SetCustomerLimitsCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(SetCustomerLimitsCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        user.SetLimits(new LimitOverrides { Accounts = c.Accounts, Posts = c.Posts, Devices = c.Devices, Seats = c.Seats });
        audit.Record(AuditAction.LimitsChanged, user.Id, to: AdminAudit.Limits(user.Limits));
        await uow.SaveChangesAsync(ct);
        return await customers.GetAsync(user.Id, ct);
    }
}

public sealed record SetCustomerNoteCommand(Guid CustomerId, string? Note) : ICommand<CustomerDto>;

public sealed class SetCustomerNoteCommandHandler(IUserRepository users, AdminCustomers customers, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<SetCustomerNoteCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(SetCustomerNoteCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        user.SetNote(c.Note);
        audit.Record(AuditAction.NoteChanged, user.Id, to: AdminAudit.Clip(user.Note));
        await uow.SaveChangesAsync(ct);
        return await customers.GetAsync(user.Id, ct);
    }
}

/// <summary>Unbinds one of the customer's devices (same effect as the customer doing it).</summary>
public sealed record AdminRevokeDeviceCommand(Guid CustomerId, Guid DeviceId) : ICommand<CustomerDto>;

public sealed class AdminRevokeDeviceCommandHandler(
    IUserRepository users, IWorkspaceRepository workspaces, IDeviceRepository devices, IAccountRepository accounts,
    IPostRepository posts, AdminCustomers customers, AdminAudit audit, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<AdminRevokeDeviceCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(AdminRevokeDeviceCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        var device = await devices.GetByIdAsync(c.DeviceId, ct);
        var ws = device is null ? null : await workspaces.GetByIdAsync(device.WorkspaceId, ct);
        if (device is null || ws?.OwnerId != user.Id) throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var now = clock.GetUtcNow();
        (await accounts.GetByDeviceAsync(device.Id, ct))?.Disconnect();
        foreach (var p in await posts.ListClaimedByAsync(device.Id, ct))
            p.Fail(FailureCode.Network, "ผู้ดูแลแพลตฟอร์มยกเลิกการผูกอุปกรณ์ระหว่างโพสต์", now);
        devices.Remove(device);
        audit.Record(AuditAction.DeviceRevoked, user.Id, to: AdminAudit.Clip(device.Name));
        await uow.SaveChangesAsync(ct);
        return await customers.GetAsync(user.Id, ct);
    }
}

/// <summary>Puts every failed post of the customer back in the queue; returns how many.</summary>
public sealed record RetryCustomerFailedCommand(Guid CustomerId) : ICommand<int>;

public sealed class RetryCustomerFailedCommandHandler(
    IUserRepository users, IWorkspaceRepository workspaces, IPostRepository posts, AdminAudit audit, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RetryCustomerFailedCommand, int>
{
    public async Task<int> HandleAsync(RetryCustomerFailedCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        var ids = (await workspaces.ListByOwnerAsync(user.Id, ct)).Select(w => w.Id);
        var failed = await posts.ListFailedAsync(ids, ct);
        var now = clock.GetUtcNow();
        foreach (var p in failed) p.Retry(now);
        audit.Record(AuditAction.FailedRetried, user.Id, to: failed.Count.ToString());
        await uow.SaveChangesAsync(ct);
        return failed.Count;
    }
}

public sealed record GetTransactionsQuery : IQuery<IReadOnlyList<TransactionDto>>;

public sealed class GetTransactionsQueryHandler(ITransactionRepository transactions)
    : IQueryHandler<GetTransactionsQuery, IReadOnlyList<TransactionDto>>
{
    public async Task<IReadOnlyList<TransactionDto>> HandleAsync(GetTransactionsQuery q, CancellationToken ct = default) =>
        (await transactions.ListAsync(DateTimeOffset.MinValue, ct)).Select(TransactionDto.From).ToList();
}

/// <summary>
/// Refunds a charge (TransactionId), or the customer's latest charge, through Stripe, and records the refund.
/// Only charges that were paid at Stripe can be refunded; a partial refund made in the Stripe dashboard counts
/// as the refund (the rest is refunded there too).
/// </summary>
public sealed record RefundCommand(Guid? TransactionId, Guid? CustomerId) : ICommand<TransactionDto>;

public sealed class RefundCommandHandler(
    ITransactionRepository transactions, IPaymentGateway gateway, AdminAudit audit, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RefundCommand, TransactionDto>
{
    public async Task<TransactionDto> HandleAsync(RefundCommand c, CancellationToken ct = default)
    {
        Transaction? charge;
        if (c.TransactionId is { } id) charge = await transactions.GetAsync(id, ct) ?? throw new NotFoundException("รายการ", id);
        else
        {
            IReadOnlyList<Transaction> mine = c.CustomerId is { } cid ? await transactions.ListByUserAsync(cid, ct) : [];
            charge = mine.FirstOrDefault(t => t.Type == TransactionType.Charge) ?? throw new DomainException("ลูกค้านี้ยังไม่มีรายการเรียกเก็บเงิน");
        }
        var all = await transactions.ListByUserAsync(charge.UserId, ct);
        if (all.Any(t => t.RefundOfId == charge.Id)) throw new ConflictException("รายการนี้คืนเงินไปแล้ว");
        if (!charge.CanRefund) throw new DomainException("รายการนี้ไม่ได้ชำระผ่าน Stripe หรือไม่มียอดเรียกเก็บ จึงคืนเงินผ่านระบบไม่ได้");

        var done = await gateway.RefundAsync(charge.StripePaymentIntentId!, charge.Amount, $"refund:{charge.Id}", ct);
        var refund = charge.RefundOf(clock.GetUtcNow(), charge.Amount, done.Id);
        transactions.Add(refund);
        audit.Record(AuditAction.Refunded, charge.UserId, charge.Id.ToString(), Money.Text(refund.Amount));
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException)
        {
            // Stripe's refund event was recorded between the call and the save: the refund is in the ledger already.
            throw new ConflictException("รายการนี้คืนเงินไปแล้ว");
        }
        return TransactionDto.From(refund);
    }
}

/// <summary>Asks Stripe to collect a failed invoice again with the customer's current card; the ledger row turns into a paid charge.</summary>
public sealed record RetryPaymentCommand(Guid TransactionId) : ICommand<TransactionDto>;

public sealed class RetryPaymentCommandHandler(
    ITransactionRepository transactions, PaymentSync sync, IPaymentGateway gateway, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<RetryPaymentCommand, TransactionDto>
{
    public async Task<TransactionDto> HandleAsync(RetryPaymentCommand c, CancellationToken ct = default)
    {
        var tx = await transactions.GetAsync(c.TransactionId, ct) ?? throw new NotFoundException("รายการ", c.TransactionId);
        if (tx.Type != TransactionType.Failed || tx.StripeInvoiceId is not { } invoiceId)
            throw new DomainException("เรียกเก็บซ้ำได้เฉพาะรายการที่ล้มเหลวและมีใบแจ้งหนี้ใน Stripe");
        var invoice = await gateway.PayInvoiceAsync(invoiceId, ct);
        await sync.RecordInvoicePaidAsync(invoice, ct);
        audit.Record(AuditAction.PaymentRetried, tx.UserId, tx.Id.ToString(), Money.Text(tx.Amount));
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException)
        {
            // Stripe's invoice.paid event got there first.
        }
        return TransactionDto.From(tx);
    }
}

public sealed record UpdatePlanCommand(PlanKey Key, int Price, int? Accounts, int? Posts, int? Devices, int? Seats) : ICommand<PlanDto>;

public sealed class UpdatePlanCommandHandler(IPlanRepository plans, AdminAudit audit, IUnitOfWork uow) : ICommandHandler<UpdatePlanCommand, PlanDto>
{
    public async Task<PlanDto> HandleAsync(UpdatePlanCommand c, CancellationToken ct = default)
    {
        var plan = await plans.GetAsync(c.Key, ct);
        var from = AdminAudit.Plan(plan);
        plan.Update(c.Price, c.Accounts, c.Posts, c.Devices, c.Seats);
        audit.Record(AuditAction.PlanSettingsChanged, null, from, AdminAudit.Plan(plan));
        await uow.SaveChangesAsync(ct);
        return PlanDto.From(plan);
    }
}

public sealed record GetPromosQuery : IQuery<IReadOnlyList<PromoDto>>;

public sealed class GetPromosQueryHandler(IPromoRepository promos) : IQueryHandler<GetPromosQuery, IReadOnlyList<PromoDto>>
{
    public async Task<IReadOnlyList<PromoDto>> HandleAsync(GetPromosQuery q, CancellationToken ct = default) =>
        (await promos.ListAsync(ct)).Select(PromoDto.From).ToList();
}

/// <summary>A new code; without an end date it runs to the end of this year (Thai calendar).</summary>
public sealed record CreatePromoCommand(string Code, string Discount, DateTimeOffset? ExpiresAt) : ICommand<PromoDto>;

public sealed class CreatePromoCommandHandler(IPromoRepository promos, AdminAudit audit, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreatePromoCommand, PromoDto>
{
    public async Task<PromoDto> HandleAsync(CreatePromoCommand c, CancellationToken ct = default)
    {
        if (await promos.GetByCodeAsync(Promo.NormalizeCode(c.Code), ct) is not null) throw new ConflictException("มีโค้ดนี้อยู่แล้ว");
        var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(7));
        var end = c.ExpiresAt ?? new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, now.Offset);
        var promo = Promo.Create(c.Code, c.Discount, end.ToUniversalTime());
        promos.Add(promo);
        audit.Record(AuditAction.PromoCreated, null, promo.Code, promo.Discount);
        await uow.SaveChangesAsync(ct);
        return PromoDto.From(promo);
    }
}

public sealed record SetPromoActiveCommand(string Code, bool Active) : ICommand<PromoDto>;

public sealed class SetPromoActiveCommandHandler(IPromoRepository promos, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<SetPromoActiveCommand, PromoDto>
{
    public async Task<PromoDto> HandleAsync(SetPromoActiveCommand c, CancellationToken ct = default)
    {
        var promo = await promos.GetByCodeAsync(Promo.NormalizeCode(c.Code), ct) ?? throw new NotFoundException("โค้ดส่วนลด", c.Code);
        promo.SetActive(c.Active);
        audit.Record(AuditAction.PromoToggled, null, promo.Code, c.Active ? "active" : "inactive");
        await uow.SaveChangesAsync(ct);
        return PromoDto.From(promo);
    }
}

/// <summary>
/// Signs the admin in as a customer for an hour (the "assist" button). The token carries the admin's id,
/// and the API only answers reads for it (ReadOnlyImpersonationMiddleware).
/// </summary>
public sealed record ImpersonateCommand(Guid CustomerId) : ICommand<AuthResultDto>;

public sealed class ImpersonateCommandHandler(
    IUserRepository users, ITokenService tokens, ICurrentUser current, AdminAudit audit, IUnitOfWork uow)
    : ICommandHandler<ImpersonateCommand, AuthResultDto>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public async Task<AuthResultDto> HandleAsync(ImpersonateCommand c, CancellationToken ct = default)
    {
        var user = await AdminCustomers.RequireAsync(users, c.CustomerId, ct);
        var token = tokens.CreateImpersonation(user, current.UserId, Lifetime);
        audit.Record(AuditAction.Impersonated, user.Id);
        await uow.SaveChangesAsync(ct);
        return new AuthResultDto(token.Token, token.ExpiresAt, UserDto.From(user));
    }
}

/// <summary>The activity log, newest first: one customer's, or the whole platform's.</summary>
public sealed record GetAuditQuery(Guid? CustomerId, int Take) : IQuery<IReadOnlyList<AuditEntryDto>>;

public sealed class GetAuditQueryHandler(IAuditRepository audit, IUserRepository users)
    : IQueryHandler<GetAuditQuery, IReadOnlyList<AuditEntryDto>>
{
    public async Task<IReadOnlyList<AuditEntryDto>> HandleAsync(GetAuditQuery q, CancellationToken ct = default)
    {
        var list = await audit.ListAsync(q.CustomerId, Math.Clamp(q.Take, 1, 200), ct);
        var ids = list.Select(e => e.ActorId).Concat(list.Where(e => e.CustomerId is not null).Select(e => e.CustomerId!.Value)).Distinct();
        var people = (await users.ListByIdsAsync(ids, ct)).ToDictionary(u => u.Id);
        return list.Select(e => new AuditEntryDto(
                e.Id, e.At, e.Action, e.ActorId, people.GetValueOrDefault(e.ActorId)?.Email ?? "",
                e.CustomerId, e.CustomerId is { } cid ? people.GetValueOrDefault(cid)?.Email : null, e.From, e.To))
            .ToList();
    }
}

/// <summary>Writes activity-log entries for the signed-in admin; saved with the handler's unit of work.</summary>
public sealed class AdminAudit(IAuditRepository audit, ICurrentUser current, TimeProvider clock)
{
    public void Record(AuditAction action, Guid? customerId, string? from = null, string? to = null) =>
        audit.Add(AuditEntry.Create(current.ImpersonatorId ?? current.UserId, customerId, action, clock.GetUtcNow(), from, to));

    public void PlanChange(User customer, PlanKey from) =>
        audit.Add(AuditEntry.PlanChange(current.ImpersonatorId ?? current.UserId, customer, from, clock.GetUtcNow()));

    public static string Key<T>(T value) where T : struct, Enum => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>"accounts=5 posts=- devices=0 seats=-": "-" keeps the plan's value, 0 = unlimited.</summary>
    public static string Limits(LimitOverrides l) =>
        $"accounts={N(l.Accounts)} posts={N(l.Posts)} devices={N(l.Devices)} seats={N(l.Seats)}";

    public static string Plan(PlanSetting p) =>
        $"{AuditEntry.Key(p.Key)} price={p.Price} accounts={N(p.Accounts)} posts={N(p.Posts)} devices={N(p.Devices)} seats={N(p.Seats)}";

    public static string? Clip(string? text) => text is { Length: > 200 } ? text[..200] : text;

    private static string N(int? n) => n?.ToString() ?? "-";
}

/// <summary>Builds the customer rows of the admin pages from users, workspaces, devices and posts.</summary>
public sealed class AdminCustomers(
    IUserRepository users, IWorkspaceRepository workspaces, IAccountRepository accounts, IMemberRepository members,
    IDeviceRepository devices, IPostRepository posts, TimeProvider clock)
{
    public static async Task<User> RequireAsync(IUserRepository users, Guid id, CancellationToken ct)
    {
        var u = await users.GetByIdAsync(id, ct);
        return u is { Role: UserRole.User } ? u : throw new NotFoundException("ลูกค้า", id);
    }

    public async Task<CustomerDto> GetAsync(Guid id, CancellationToken ct) => (await ListAsync(id, ct)).Single();

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(Guid? only, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var people = (await users.ListAsync(ct)).Where(u => u.Role == UserRole.User && (only is null || u.Id == only)).ToList();
        var allWs = (await workspaces.ListAllAsync(ct)).Where(w => people.Any(p => p.Id == w.OwnerId)).ToList();
        var wsIds = allWs.Select(w => w.Id).ToList();
        var connected = await accounts.ListConnectedAsync(wsIds, ct);
        var team = await members.ListByWorkspacesAsync(wsIds, ct);
        var devs = await devices.ListByWorkspacesAsync(wsIds, ct);
        var counts = await posts.CountByStatusAsync(wsIds, now.AddDays(-1), now.AddDays(30), ct);

        return people.OrderByDescending(u => u.CreatedAt).Select(u =>
        {
            var mine = allWs.Where(w => w.OwnerId == u.Id).Select(w => w.Id).ToHashSet();
            var myDevices = devs.Where(d => mine.Contains(d.WorkspaceId)).OrderByDescending(d => d.LastSeenAt).ToList();
            int Count(PostStatus s) => counts.Where(x => mine.Contains(x.WorkspaceId) && x.Status == s).Sum(x => x.Count);
            var lastActive = new[] { u.LastSeenAt }.Concat(myDevices.Select(d => d.LastSeenAt)).Max();
            return new CustomerDto(
                u.Id, u.Name, u.Email, u.Plan, u.Status, u.CreatedAt, u.Cycle,
                connected.Count(a => mine.Contains(a.WorkspaceId)),
                mine.Count == 0 ? 1 : 1 + team.Where(m => mine.Contains(m.WorkspaceId) && m.IsActive).Select(m => m.UserId).Distinct().Count(),
                myDevices.FirstOrDefault()?.Version ?? "",
                lastActive, u.Paused,
                new CustomerJobsDto(Count(PostStatus.Success) + Count(PostStatus.Pending), Count(PostStatus.Failed), Count(PostStatus.Queued), Count(PostStatus.Posting)),
                myDevices.Select(d => new CustomerDeviceDto(d.Id, d.Name, d.Browser, d.LastSeenAt, d.IsOnline(now))).ToList(),
                u.Note, mine.Count,
                new LimitOverridesDto(u.Limits.Accounts, u.Limits.Posts, u.Limits.Devices, u.Limits.Seats),
                u.HasSubscription, u.PlanRenewsAt, u.CancelAtPeriodEnd);
        }).ToList();
    }
}
