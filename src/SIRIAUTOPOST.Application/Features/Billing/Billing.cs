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
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ChangePlanCommand, UserDto>
{
    public async Task<UserDto> HandleAsync(ChangePlanCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        var cycle = c.Cycle ?? user.Cycle;
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
        await uow.SaveChangesAsync(ct);
        return UserDto.From(user);
    }
}
