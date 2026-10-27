using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Billing;

public sealed record GetBillingProfileQuery : IQuery<BillingProfileDto>;

public sealed class GetBillingProfileQueryHandler(IUserRepository users, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetBillingProfileQuery, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(GetBillingProfileQuery q, CancellationToken ct = default) =>
        BillingProfiles.Dto(await BillingProfiles.UserAsync(users, current, ct), clock.GetUtcNow());
}

public sealed record UpdateBillingNotificationsCommand(bool NotifyFailed, bool NotifyExpiring, bool NotifyRenewal)
    : ICommand<BillingProfileDto>;

public sealed class UpdateBillingNotificationsCommandHandler(
    IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdateBillingNotificationsCommand, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(UpdateBillingNotificationsCommand command, CancellationToken ct = default)
    {
        var user = await BillingProfiles.UserAsync(users, current, ct);
        user.SetBillingNotifications(command.NotifyFailed, command.NotifyExpiring, command.NotifyRenewal);
        await uow.SaveChangesAsync(ct);
        return BillingProfiles.Dto(user, clock.GetUtcNow());
    }
}

public sealed record SetPaymentMethodCommand(string? Brand, string Last4, int ExpMonth, int ExpYear)
    : ICommand<BillingProfileDto>;

public sealed class SetPaymentMethodCommandHandler(
    IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetPaymentMethodCommand, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(SetPaymentMethodCommand command, CancellationToken ct = default)
    {
        var user = await BillingProfiles.UserAsync(users, current, ct);
        var now = clock.GetUtcNow();
        user.SetPaymentMethod(command.Brand, command.Last4, command.ExpMonth, command.ExpYear, now);
        await uow.SaveChangesAsync(ct);
        return BillingProfiles.Dto(user, now);
    }
}

public sealed record RemovePaymentMethodCommand : ICommand<BillingProfileDto>;

public sealed class RemovePaymentMethodCommandHandler(
    IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RemovePaymentMethodCommand, BillingProfileDto>
{
    public async Task<BillingProfileDto> HandleAsync(RemovePaymentMethodCommand command, CancellationToken ct = default)
    {
        var user = await BillingProfiles.UserAsync(users, current, ct);
        user.RemovePaymentMethod();
        await uow.SaveChangesAsync(ct);
        return BillingProfiles.Dto(user, clock.GetUtcNow());
    }
}

public sealed record GetStatementQuery(Guid TransactionId) : IQuery<StatementDto>;

public sealed class GetStatementQueryHandler(
    ITransactionRepository transactions, IUserRepository users, ICurrentUser current)
    : IQueryHandler<GetStatementQuery, StatementDto>
{
    public async Task<StatementDto> HandleAsync(GetStatementQuery query, CancellationToken ct = default)
    {
        var transaction = await transactions.GetAsync(query.TransactionId, ct);
        if (transaction is null || transaction.UserId != current.UserId)
            throw new NotFoundException("รายการ", query.TransactionId);

        var user = await BillingProfiles.UserAsync(users, current, ct);
        return new StatementDto(
            transaction.Id, transaction.CreatedAt, transaction.Type, transaction.Amount, transaction.Plan,
            transaction.Cycle, transaction.PromoCode, user.Name, user.Email);
    }
}

internal static class BillingProfiles
{
    public static async Task<User> UserAsync(IUserRepository users, ICurrentUser current, CancellationToken ct) =>
        await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");

    public static BillingProfileDto Dto(User user, DateTimeOffset now)
    {
        var billing = user.Billing;
        PaymentMethodDto? card = null;
        if (billing is { HasCard: true, CardValidThrough: { } through })
        {
            card = new PaymentMethodDto(
                billing.CardBrand ?? "card",
                billing.CardLast4!,
                billing.CardExpMonth!.Value,
                billing.CardExpYear!.Value,
                Expired: through <= now,
                ExpiresSoon: through > now && through <= now.AddMonths(1));
        }

        return new BillingProfileDto(
            billing.NotifyFailed, billing.NotifyExpiring, billing.NotifyRenewal, card, PaymentsConnected: false);
    }
}
