using SIRIAUTOPOST.Application.Common;
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

/// <summary>Plan, renewal date, card, limits and usage of the signed-in customer.</summary>
public sealed record GetBillingQuery : IQuery<BillingDto>;

public sealed class GetBillingQueryHandler(
    IUserRepository users, IPlanRepository plans, IWorkspaceRepository workspaces, IAccountRepository accounts, IPostRepository posts,
    IDeviceRepository devices, IPaymentGateway gateway, PlanQuotas quotas, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetBillingQuery, BillingDto>
{
    public async Task<BillingDto> HandleAsync(GetBillingQuery q, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        var known = gateway.Enabled && user.StripeCustomerId is not null;
        var card = known ? await gateway.GetCardAsync(user.StripeCustomerId!, ct) : null;

        var limits = await plans.ForAsync(user, ct);
        var own = (await workspaces.ListByOwnerAsync(user.Id, ct)).Select(w => w.Id).ToList();
        var perWorkspace = (await devices.ListByWorkspacesAsync(own, ct)).GroupBy(d => d.WorkspaceId).Select(g => g.Count());
        var (groups, images, libraryPosts) = await quotas.UsageOfOwnerAsync(own, ct);
        var usage = new UsageDto(
            await accounts.CountConnectedAsync(own, ct),
            await posts.CountPublishedSinceAsync(own, clock.GetUtcNow().AddDays(-1), ct),
            perWorkspace.DefaultIfEmpty(0).Max(), groups, images, libraryPosts);

        return new BillingDto(
            gateway.Enabled, user.Plan, user.Cycle, user.Status, user.HasSubscription, user.PlanRenewsAt, user.CancelAtPeriodEnd, known,
            card is null ? null : new CardDto(card.Brand, card.Last4, card.ExpMonth, card.ExpYear), LimitsDto.From(limits), usage);
    }
}

/// <summary>
/// Choosing a plan. Free ends the Stripe subscription at the end of the paid period (the plan stays until then).
/// A paid plan for a customer with no subscription returns the Stripe Checkout page to pay at; the plan changes
/// when Stripe confirms the payment. A customer who already has a subscription moves to the new plan at once,
/// the difference prorated and invoiced by Stripe (choosing the current plan again takes back a scheduled
/// cancellation). A promo code applies to the first invoice only. A customer the admin listed for a payment test
/// (<see cref="PaymentOverride"/>) is charged the test amount for the period instead of the plan's price.
/// </summary>
public sealed record ChangePlanCommand(PlanKey Plan, BillingCycle? Cycle, string? PromoCode) : ICommand<PlanChangeDto>;

public sealed class ChangePlanCommandHandler(
    IUserRepository users, IPlanRepository plans, IPromoRepository promos, IAuditRepository audit, IPaymentOverrideRepository overrides,
    IPaymentGateway gateway, PaymentSync sync, IAppUrls urls, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ChangePlanCommand, PlanChangeDto>
{
    public const string NotReady = "ระบบชำระเงิน (Stripe) ยังไม่พร้อมใช้งาน ติดต่อผู้ดูแลแพลตฟอร์ม";

    public async Task<PlanChangeDto> HandleAsync(ChangePlanCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        var cycle = c.Cycle ?? user.Cycle;
        var plan = await plans.GetAsync(c.Plan, ct);
        var promoCode = Promo.NormalizeCode(c.PromoCode);

        string? checkoutUrl = null;
        if (plan.Key == PlanKey.Free)
        {
            if (promoCode.Length > 0) throw new DomainException("ใช้โค้ดส่วนลดได้กับแผนที่มีค่าใช้จ่ายเท่านั้น");
            await GoFreeAsync(user, now, ct);
        }
        else if (user.HasSubscription)
        {
            if (promoCode.Length > 0) throw new DomainException("ใช้โค้ดส่วนลดได้ตอนสมัครแผนครั้งแรกเท่านั้น");
            await MoveSubscriptionAsync(user, plan, cycle, await TestAmountAsync(user, promoCode, ct), now, ct);
        }
        else checkoutUrl = await StartCheckoutAsync(user, plan, cycle, promoCode, await TestAmountAsync(user, promoCode, ct), now, ct);

        await uow.SaveChangesAsync(ct);
        return new PlanChangeDto(UserDto.From(user), checkoutUrl);
    }

    /// <summary>The admin's test amount for this customer (null = the plan's price). It replaces the price, so a promo code cannot go with it.</summary>
    private async Task<int?> TestAmountAsync(User user, string promoCode, CancellationToken ct)
    {
        var amount = (await overrides.GetAsync(ct))?.ChargeFor(user.Email);
        if (amount is not null && promoCode.Length > 0) throw new DomainException("ใช้โค้ดส่วนลดไม่ได้ขณะที่บัญชีนี้อยู่ในโหมดทดสอบการชำระเงิน");
        return amount;
    }

    private async Task GoFreeAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        if (user.HasSubscription)
        {
            if (user.CancelAtPeriodEnd) return;
            var snapshot = await gateway.SetCancelAtPeriodEndAsync(user.StripeSubscriptionId!, true, ct);
            sync.ApplySubscription(user, snapshot, user.Id);
        }
        else if (user.IsPrepaid)
        {
            // Paid up to its end date (PromptPay) and nothing renews it: choosing Free just lets it run out.
        }
        else if (user.Plan != PlanKey.Free)
        {
            var from = user.Plan;
            user.DowngradeToFree(); // a plan the admin granted: nothing to cancel
            audit.Add(AuditEntry.PlanChange(user.Id, user, from, now));
        }
    }

    private async Task MoveSubscriptionAsync(User user, PlanSetting plan, BillingCycle cycle, int? testAmount, DateTimeOffset now, CancellationToken ct)
    {
        if (!gateway.Enabled) throw new DomainException(NotReady);
        var id = user.StripeSubscriptionId!;
        SubscriptionSnapshot snapshot;
        if (plan.Key == user.Plan && cycle == user.Cycle)
        {
            if (!user.CancelAtPeriodEnd) return; // already on it
            snapshot = await gateway.SetCancelAtPeriodEndAsync(id, false, ct);
        }
        else
        {
            snapshot = await gateway.ChangeSubscriptionAsync(id, plan.Key, cycle, plan.Price, testAmount, ct);
            RecordTestAmount(user, plan.Key, testAmount, now);
        }
        sync.ApplySubscription(user, snapshot, user.Id);
    }

    private async Task<string> StartCheckoutAsync(
        User user, PlanSetting plan, BillingCycle cycle, string promoCode, int? testAmount, DateTimeOffset now, CancellationToken ct)
    {
        if (!gateway.Enabled) throw new DomainException(NotReady);
        Promo? promo = null;
        if (promoCode.Length > 0)
        {
            promo = await promos.GetByCodeAsync(promoCode, ct) ?? throw new DomainException("ไม่พบโค้ดส่วนลดนี้");
            promo.EnsureUsable(now);
        }
        var customer = user.StripeCustomerId;
        if (customer is null)
        {
            customer = await gateway.EnsureCustomerAsync(user, ct);
            user.LinkStripeCustomer(customer);
        }
        var web = urls.WebBase;
        var discount = promo is null ? 0 : Pricing.FirstDiscount(plan.Price, cycle, promo.Discount);
        var url = await gateway.CreateCheckoutAsync(new CheckoutRequest(
            user.Id, customer, plan.Key, cycle, plan.Price, discount, promo?.Code,
            SuccessUrl: $"{web}/app/billing?checkout=success&session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl: $"{web}/app/billing?checkout=cancel",
            ChargeOverride: testAmount), ct);
        RecordTestAmount(user, plan.Key, testAmount, now);
        return url;
    }

    /// <summary>The activity log says when a customer was charged the test amount instead of the plan's price (saved with the handler's unit of work).</summary>
    private void RecordTestAmount(User user, PlanKey plan, int? testAmount, DateTimeOffset now)
    {
        if (testAmount is { } amount) audit.Add(AuditEntry.Create(user.Id, user.Id, AuditAction.PaymentOverrideUsed, now, AuditEntry.Key(plan), Money.Text(amount)));
    }
}

/// <summary>
/// The customer came back from Stripe Checkout: reads the session from Stripe and applies it, the same way the
/// webhook does (whichever comes first wins; the other finds it done). Makes the plan show at once, and works
/// where webhooks cannot reach the API (a laptop).
/// </summary>
public sealed record ConfirmCheckoutCommand(string SessionId) : ICommand<UserDto>;

public sealed class ConfirmCheckoutCommandHandler(
    IUserRepository users, IPaymentGateway gateway, PaymentSync sync, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<ConfirmCheckoutCommand, UserDto>
{
    public async Task<UserDto> HandleAsync(ConfirmCheckoutCommand c, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        var session = await gateway.GetCheckoutSessionAsync(c.SessionId, ct);
        // Someone else's session looks like a missing one.
        if (session.UserId != user.Id) throw new NotFoundException("การชำระเงิน", c.SessionId);
        if (!session.Paid) throw new DomainException("การชำระเงินยังไม่เสร็จสมบูรณ์");
        await sync.CompleteCheckoutAsync(session, user.Id, ct);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException)
        {
            // The webhook applied the same session a moment ago; the state we hold is the same.
        }
        return UserDto.From(user);
    }
}

/// <summary>The Stripe Billing Portal page: update the card, see invoices, cancel.</summary>
public sealed record CreatePortalSessionCommand : ICommand<UrlDto>;

public sealed class CreatePortalSessionCommandHandler(IUserRepository users, IPaymentGateway gateway, IAppUrls urls, ICurrentUser current)
    : ICommandHandler<CreatePortalSessionCommand, UrlDto>
{
    public async Task<UrlDto> HandleAsync(CreatePortalSessionCommand c, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        if (!gateway.Enabled) throw new DomainException(ChangePlanCommandHandler.NotReady);
        if (user.StripeCustomerId is not { } customer) throw new DomainException("ยังไม่มีข้อมูลการชำระเงิน เลือกแผนที่มีค่าใช้จ่ายก่อน");
        return new UrlDto(await gateway.CreatePortalAsync(customer, $"{urls.WebBase}/app/billing", ct));
    }
}

/// <summary>A Stripe webhook delivery. Signature first, then each event is applied once.</summary>
public sealed record StripeWebhookCommand(string Payload, string? Signature) : ICommand<Unit>;

public sealed class StripeWebhookCommandHandler(
    IPaymentGateway gateway, PaymentSync sync, PaymentIntentSync intents, IUserRepository users, IPaymentEventRepository processed,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<StripeWebhookCommand, Unit>
{
    public async Task<Unit> HandleAsync(StripeWebhookCommand c, CancellationToken ct = default)
    {
        var evt = gateway.ParseWebhook(c.Payload, c.Signature);
        if (evt is null || await processed.ExistsAsync(evt.Id, ct)) return Unit.Value;

        switch (evt)
        {
            case CheckoutCompletedEvent e:
                if (e.Session.Paid) await sync.CompleteCheckoutAsync(e.Session, PaymentSync.System, ct);
                break;
            case SubscriptionChangedEvent e:
                // Re-read the subscription: events can arrive late or out of order, Stripe's current state is the truth.
                if (await users.GetByStripeCustomerAsync(e.CustomerId, ct) is { } owner)
                    sync.ApplySubscription(owner, await gateway.GetSubscriptionAsync(e.SubscriptionId, ct), PaymentSync.System);
                break;
            case InvoicePaidEvent e:
                await sync.RecordInvoicePaidAsync(e.Invoice, ct);
                break;
            case InvoiceFailedEvent e:
                await sync.RecordInvoiceFailedAsync(e.Invoice, ct);
                break;
            case RefundEvent e:
                await sync.RecordRefundAsync(e.Refund, ct);
                break;
            case PaymentIntentEvent e:
                // The in-app checkout's payments (pending, paid, failed). Ones that are not ours (renewals) are ignored.
                await intents.ApplyAsync(e.PaymentIntentId, ct);
                break;
        }

        processed.Add(ProcessedPaymentEvent.Create(evt.Id, evt.GetType().Name, clock.GetUtcNow()));
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException)
        {
            // The same delivery (or the Checkout return) got there first and committed the same result.
        }
        return Unit.Value;
    }
}
