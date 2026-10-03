using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Features.Billing;

/// <summary>Prices and limits of every plan (public: the landing page shows them).</summary>
public sealed record GetPlansQuery : IQuery<IReadOnlyList<PlanDto>>;

public sealed class GetPlansQueryHandler(IPlanRepository plans) : IQueryHandler<GetPlansQuery, IReadOnlyList<PlanDto>>
{
    public async Task<IReadOnlyList<PlanDto>> HandleAsync(GetPlansQuery q, CancellationToken ct = default) =>
        (await plans.ListAsync(ct)).Select(PlanDto.From).ToList();
}

/// <summary>The signed-in customer's charges and refunds, newest first.</summary>
public sealed record GetInvoicesQuery : IQuery<IReadOnlyList<TransactionDto>>;

public sealed class GetInvoicesQueryHandler(ITransactionRepository transactions, ICurrentUser current)
    : IQueryHandler<GetInvoicesQuery, IReadOnlyList<TransactionDto>>
{
    public async Task<IReadOnlyList<TransactionDto>> HandleAsync(GetInvoicesQuery q, CancellationToken ct = default) =>
        (await transactions.ListByUserAsync(current.UserId, ct)).Select(TransactionDto.From).ToList();
}

/// <summary>
/// Self-service plan change. A paid plan records a charge for one billing period (yearly = 12 months at 80%),
/// less a promo code's discount. No payment provider is connected: the charge is recorded, not collected.
/// </summary>
public sealed record ChangePlanCommand(PlanKey Plan, BillingCycle? Cycle, string? PromoCode) : ICommand<UserDto>;

public sealed class ChangePlanCommandHandler(
    IUserRepository users, IPlanRepository plans, IPromoRepository promos, ITransactionRepository transactions,
    IAuditRepository audit, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ChangePlanCommand, UserDto>
{
    public async Task<UserDto> HandleAsync(ChangePlanCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        var cycle = c.Cycle ?? user.Cycle;
        var from = user.Plan;
        var plan = await plans.GetAsync(c.Plan, ct);
        Promo? promo = null;
        if (!string.IsNullOrWhiteSpace(c.PromoCode))
        {
            promo = await promos.GetByCodeAsync(Promo.NormalizeCode(c.PromoCode), ct) ?? throw new DomainException("ไม่พบโค้ดส่วนลดนี้");
            if (plan.Price == 0) throw new DomainException("ใช้โค้ดส่วนลดได้กับแผนที่มีค่าใช้จ่ายเท่านั้น");
            promo.Use(now);
        }
        var amount = Pricing.Charge(plan.Price, cycle, promo?.Discount);
        var paid = plan.Price > 0;
        if (paid) transactions.Add(Transaction.Charge(user.Id, amount, plan.Key, cycle, promo?.Code, now));
        user.ChangePlan(plan.Key, cycle, paid);
        if (from != plan.Key) audit.Add(AuditEntry.PlanChange(user.Id, user, from, now));
        await uow.SaveChangesAsync(ct);
        return UserDto.From(user);
    }
}

/// <summary>Billing preferences and the card on file.</summary>
public sealed record GetBillingProfileQuery : IQuery<BillingProfileDto>;

public sealed class GetBillingProfileQueryHandler(IUserRepository users, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetBillingProfileQuery, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(GetBillingProfileQuery q, CancellationToken ct = default) =>
        BillingProfiles.Dto(await BillingProfiles.UserAsync(users, current, ct), clock.GetUtcNow());
}

public sealed record UpdateBillingNotificationsCommand(bool NotifyFailed, bool NotifyExpiring, bool NotifyRenewal) : ICommand<BillingProfileDto>;

public sealed class UpdateBillingNotificationsCommandHandler(IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateBillingNotificationsCommand, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(UpdateBillingNotificationsCommand c, CancellationToken ct = default)
    {
        var user = await BillingProfiles.UserAsync(users, current, ct);
        user.SetBillingNotifications(c.NotifyFailed, c.NotifyExpiring, c.NotifyRenewal);
        await uow.SaveChangesAsync(ct);
        return BillingProfiles.Dto(user, clock.GetUtcNow());
    }
}

/// <summary>
/// Saves the card the payment provider returned (brand, last four digits, expiry). The web app never sends
/// the number or the CVC, and this API has no field for them.
/// </summary>
public sealed record SetPaymentMethodCommand(string? Brand, string Last4, int ExpMonth, int ExpYear) : ICommand<BillingProfileDto>;

public sealed class SetPaymentMethodCommandHandler(IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetPaymentMethodCommand, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(SetPaymentMethodCommand c, CancellationToken ct = default)
    {
        var user = await BillingProfiles.UserAsync(users, current, ct);
        var now = clock.GetUtcNow();
        user.SetPaymentMethod(c.Brand, c.Last4, c.ExpMonth, c.ExpYear, now);
        await uow.SaveChangesAsync(ct);
        return BillingProfiles.Dto(user, now);
    }
}

public sealed record RemovePaymentMethodCommand : ICommand<BillingProfileDto>;

public sealed class RemovePaymentMethodCommandHandler(IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RemovePaymentMethodCommand, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(RemovePaymentMethodCommand c, CancellationToken ct = default)
    {
        var user = await BillingProfiles.UserAsync(users, current, ct);
        user.RemovePaymentMethod();
        await uow.SaveChangesAsync(ct);
        return BillingProfiles.Dto(user, clock.GetUtcNow());
    }
}

/// <summary>One of the signed-in customer's own charges or refunds, with who it is for.</summary>
public sealed record GetStatementQuery(Guid TransactionId) : IQuery<StatementDto>;

public sealed class GetStatementQueryHandler(ITransactionRepository transactions, IUserRepository users, ICurrentUser current)
    : IQueryHandler<GetStatementQuery, StatementDto>
{
    public async Task<StatementDto> HandleAsync(GetStatementQuery q, CancellationToken ct = default)
    {
        // Someone else's transaction is the same 404 as a missing one.
        var tx = await transactions.GetAsync(q.TransactionId, ct);
        if (tx is null || tx.UserId != current.UserId) throw new NotFoundException("รายการ", q.TransactionId);
        var user = await BillingProfiles.UserAsync(users, current, ct);
        return new StatementDto(tx.Id, tx.CreatedAt, tx.Type, tx.Amount, tx.Plan, tx.Cycle, tx.PromoCode, user.Name, user.Email);
    }
}

internal static class BillingProfiles
{
    public static async Task<User> UserAsync(IUserRepository users, ICurrentUser current, CancellationToken ct) =>
        await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");

    /// <summary>A card "expires soon" in its last month of validity and the one before.</summary>
    public static BillingProfileDto Dto(User user, DateTimeOffset now)
    {
        var b = user.Billing;
        PaymentMethodDto? card = null;
        if (b is { HasCard: true, CardValidThrough: { } through })
            card = new PaymentMethodDto(
                b.CardBrand ?? "card", b.CardLast4!, b.CardExpMonth!.Value, b.CardExpYear!.Value,
                Expired: through <= now, ExpiresSoon: through > now && through <= now.AddMonths(1));
        return new BillingProfileDto(b.NotifyFailed, b.NotifyExpiring, b.NotifyRenewal, card, PaymentsConnected: false);
    }
}
