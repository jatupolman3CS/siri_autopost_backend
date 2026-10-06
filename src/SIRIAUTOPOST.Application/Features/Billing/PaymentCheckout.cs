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

// The in-app payment window: five ways to pay (card, Apple Pay, Google Pay, Link, PromptPay) on the billing page
// instead of Stripe's hosted Checkout. The server makes the Stripe PaymentIntent, the browser confirms it with
// Stripe.js, and the plan only changes from what Stripe says (webhook or status call, whichever comes first).

/// <summary>What the browser needs to start Stripe.js (the publishable key, public by design).</summary>
public sealed record GetPaymentConfigQuery : IQuery<PaymentConfigDto>;

public sealed class GetPaymentConfigQueryHandler(IPaymentConfig config) : IQueryHandler<GetPaymentConfigQuery, PaymentConfigDto>
{
    public Task<PaymentConfigDto> HandleAsync(GetPaymentConfigQuery q, CancellationToken ct = default) =>
        Task.FromResult(new PaymentConfigDto(config.PublishableKey, config.Currency));
}

/// <summary>
/// Starts paying for a plan with one of the five methods. Card, Apple Pay, Google Pay and Link pay a Stripe
/// subscription (the plan renews by itself). PromptPay is single-use at Stripe and cannot be put on a subscription,
/// so it pays one period of the plan up front (the plan then ends by itself). A promo code takes its discount off this
/// first payment; a customer the admin listed for a payment test (<see cref="PaymentOverride"/>) pays the test amount.
/// </summary>
public sealed record StartPaymentCommand(PlanKey Plan, BillingCycle? Cycle, string? PromoCode, PaymentMethodKind Method) : ICommand<PaymentIntentDto>;

public sealed class StartPaymentCommandHandler(
    IUserRepository users, IPlanRepository plans, IPromoRepository promos, IAuditRepository audit, IPaymentOverrideRepository overrides,
    IPaymentAttemptRepository attempts, IPaymentGateway gateway, IPaymentConfig config, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<StartPaymentCommand, PaymentIntentDto>
{
    public async Task<PaymentIntentDto> HandleAsync(StartPaymentCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        if (!gateway.Enabled) throw new DomainException(ChangePlanCommandHandler.NotReady);
        var plan = await plans.GetAsync(c.Plan, ct);
        if (plan.Key == PlanKey.Free) throw new DomainException("แผน Free ไม่มีค่าใช้จ่าย เลือกแผนที่เสียเงินเพื่อชำระเงิน");
        if (user.HasSubscription) throw new DomainException("คุณมีการสมัครสมาชิกอยู่แล้ว เปลี่ยนแผนได้จากปุ่มเลือกแผนโดยไม่ต้องชำระซ้ำ");

        var cycle = c.Cycle ?? user.Cycle;
        var promoCode = Promo.NormalizeCode(c.PromoCode);
        var testAmount = (await overrides.GetAsync(ct))?.ChargeFor(user.Email);
        if (testAmount is not null && promoCode.Length > 0)
            throw new DomainException("ใช้โค้ดส่วนลดไม่ได้ขณะที่บัญชีนี้อยู่ในโหมดทดสอบการชำระเงิน");
        Promo? promo = null;
        if (promoCode.Length > 0)
        {
            promo = await promos.GetByCodeAsync(promoCode, ct) ?? throw new DomainException("ไม่พบโค้ดส่วนลดนี้");
            promo.EnsureUsable(now);
        }

        var amount = testAmount ?? Pricing.Charge(plan.Price, cycle, promo?.Discount);
        if (amount < PlanSetting.MinPaidPrice)
            throw new DomainException($"ยอดชำระหลังหักส่วนลดต่ำกว่า {PlanSetting.MinPaidPrice} บาท ซึ่งเป็นขั้นต่ำที่ Stripe เรียกเก็บได้ ลองไม่ใช้โค้ดส่วนลด");

        var customer = user.StripeCustomerId;
        if (customer is null)
        {
            customer = await gateway.EnsureCustomerAsync(user, ct);
            user.LinkStripeCustomer(customer);
        }

        var flow = c.Method == PaymentMethodKind.Promptpay ? PaymentFlow.Prepaid : PaymentFlow.Subscription;
        var attemptId = Guid.NewGuid();
        var started = flow == PaymentFlow.Prepaid
            ? await gateway.CreatePrepaidPaymentAsync(new PrepaidPaymentRequest(
                user.Id, customer, user.Email, plan.Key, cycle, amount, promo?.Code, attemptId), ct)
            : await gateway.CreateSubscriptionPaymentAsync(new SubscriptionPaymentRequest(
                user.Id, customer, plan.Key, cycle, plan.Price, promo is null ? 0 : Pricing.FirstDiscount(plan.Price, cycle, promo.Discount),
                promo?.Code, testAmount, attemptId), ct);

        attempts.Add(PaymentAttempt.Start(
            user.Id, started.IntentId, started.SubscriptionId, flow, c.Method, plan.Key, cycle, started.Amount, promo?.Code, now));
        if (testAmount is { } test)
            audit.Add(AuditEntry.Create(user.Id, user.Id, AuditAction.PaymentOverrideUsed, now, AuditEntry.Key(plan.Key), Money.Text(test)));
        await uow.SaveChangesAsync(ct);
        return new PaymentIntentDto(started.IntentId, started.ClientSecret, started.Amount, config.Currency, flow, c.Method);
    }
}

/// <summary>
/// The browser says Stripe.js finished (or asks again while a PromptPay payment is pending): the server reads the
/// PaymentIntent from Stripe, applies it (the webhook does the same, whichever comes first wins) and answers where it stands.
/// Someone else's payment looks like a missing one.
/// </summary>
public sealed record ConfirmPaymentCommand(string IntentId) : ICommand<PaymentStatusDto>;

public sealed class ConfirmPaymentCommandHandler(
    IUserRepository users, IPaymentAttemptRepository attempts, PaymentIntentSync sync, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<ConfirmPaymentCommand, PaymentStatusDto>
{
    public async Task<PaymentStatusDto> HandleAsync(ConfirmPaymentCommand c, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        var attempt = await attempts.GetByIntentAsync(c.IntentId, ct);
        if (attempt is null || attempt.UserId != user.Id) throw new NotFoundException("การชำระเงิน", c.IntentId);
        await sync.ApplyAsync(attempt, ct);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DuplicateKeyException)
        {
            // The webhook applied the same payment a moment ago; the state we hold is the same.
        }
        return new PaymentStatusDto(
            attempt.StripePaymentIntentId, attempt.State, attempt.PaidMethod, attempt.FailureMessage, attempt.Amount, attempt.Flow, UserDto.From(user));
    }
}

/// <summary>
/// Applies what Stripe says about a PaymentIntent of the in-app checkout to its <see cref="PaymentAttempt"/> and, the
/// first time it is paid, hands over what was bought: a subscription is read again from Stripe (its own events also
/// move the plan), a prepaid PromptPay payment starts the plan for one period and is entered in the ledger. Safe to
/// run twice and out of order. Callers save through their own unit of work and treat <see cref="DuplicateKeyException"/> as done.
/// </summary>
public sealed class PaymentIntentSync(
    IPaymentAttemptRepository attempts, IUserRepository users, ITransactionRepository transactions, IPromoRepository promos,
    IPaymentEventRepository processed, IAuditRepository audit, IPaymentGateway gateway, PaymentSync sync, TimeProvider clock)
{
    /// <summary>A webhook's PaymentIntent: returns the attempt it belongs to, or null when it is not one of ours (a renewal's, say).</summary>
    public async Task<PaymentAttempt?> ApplyAsync(string paymentIntentId, CancellationToken ct)
    {
        var attempt = await attempts.GetByIntentAsync(paymentIntentId, ct);
        if (attempt is not null) await ApplyAsync(attempt, ct);
        return attempt;
    }

    /// <summary>
    /// A paid one-off Checkout session (the PromptPay page of Stripe, used when the in-app window has no publishable key):
    /// its payment gets an attempt of its own now, then goes the same way as one made in the window.
    /// </summary>
    public async Task ApplyCheckoutAsync(CheckoutSessionSnapshot session, CancellationToken ct)
    {
        var key = $"checkout:{session.Id}";
        if (await processed.ExistsAsync(key, ct)) return;
        if (session is not { UserId: { } userId, PaymentIntentId: { } intentId, Plan: { } plan, Cycle: { } cycle }) return; // not one of ours
        var now = clock.GetUtcNow();
        var attempt = await attempts.GetByIntentAsync(intentId, ct);
        if (attempt is null)
        {
            attempt = PaymentAttempt.Start(
                userId, intentId, null, PaymentFlow.Prepaid, PaymentMethodKind.Promptpay, plan, cycle, session.Amount, session.PromoCode, now);
            attempts.Add(attempt);
        }
        processed.Add(ProcessedPaymentEvent.Create(key, "checkout", now));
        await ApplyAsync(attempt, ct);
    }

    public async Task ApplyAsync(PaymentAttempt attempt, CancellationToken ct)
    {
        if (attempt.State == PaymentAttemptState.Succeeded) return; // final: nothing Stripe says later changes it
        var now = clock.GetUtcNow();
        // Stripe's current state is the truth, so an event that arrives late or out of order cannot move it back.
        var intent = await gateway.GetPaymentIntentAsync(attempt.StripePaymentIntentId, ct);
        var method = intent.Method;
        switch (intent.State)
        {
            case PaymentIntentState.Pending:
                attempt.MarkPending(method, now);
                return;
            case PaymentIntentState.Failed:
                attempt.MarkFailed(intent.FailureMessage, method, now);
                return;
        }

        SubscriptionSnapshot? subscription = null;
        if (attempt.Flow == PaymentFlow.Subscription && attempt.StripeSubscriptionId is { } id)
        {
            subscription = await gateway.GetSubscriptionAsync(id, ct);
            // Paid, but Stripe has not switched the subscription on yet: stay pending, the next look finds it active.
            if (subscription.State == SubscriptionState.Incomplete)
            {
                attempt.MarkPending(method, now);
                return;
            }
        }
        if (!attempt.MarkSucceeded(method, now)) return;

        var key = $"intent:{attempt.StripePaymentIntentId}";
        if (await processed.ExistsAsync(key, ct)) return;
        processed.Add(ProcessedPaymentEvent.Create(key, "payment_intent", now));
        var user = await users.GetByIdAsync(attempt.UserId, ct);
        if (user is null) return;

        if (subscription is not null) sync.ApplySubscription(user, subscription, PaymentSync.System);
        else StartPrepaidPeriod(user, attempt, intent, now);
        if (attempt.PromoCode is { } code && await promos.GetByCodeAsync(Promo.NormalizeCode(code), ct) is { } promo) promo.RecordUse();
    }

    private void StartPrepaidPeriod(User user, PaymentAttempt attempt, PaymentIntentSnapshot intent, DateTimeOffset now)
    {
        // The money is entered in every case, so an admin can refund it; the plan only moves when it can.
        transactions.Add(Transaction.Charge(
            user.Id, intent.Amount, attempt.Plan, attempt.Cycle, attempt.PromoCode, now, new PaymentRefs(null, intent.Id, intent.ReceiptUrl)));
        if (user.HasSubscription) return; // subscribed meanwhile: the subscription's plan stands

        // Paying again for the plan one is on adds a period after the current one ends.
        var from = user.Plan;
        var start = user is { IsPrepaid: true, PlanRenewsAt: { } ends } && user.Plan == attempt.Plan && ends > now ? ends : now;
        user.ApplyPrepaid(attempt.Plan, attempt.Cycle, attempt.Cycle == BillingCycle.Year ? start.AddYears(1) : start.AddMonths(1));
        if (from != user.Plan) audit.Add(AuditEntry.PlanChange(PaymentSync.System, user, from, now));
    }
}

/// <summary>Customers whose prepaid plan has run out go back to Free (a subscription ends through Stripe's events instead).</summary>
public sealed record EndPrepaidPlansCommand : ICommand<int>;

public sealed class EndPrepaidPlansCommandHandler(IUserRepository users, IAuditRepository audit, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<EndPrepaidPlansCommand, int>
{
    public async Task<int> HandleAsync(EndPrepaidPlansCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var ended = await users.ListPrepaidEndedAsync(now, ct);
        foreach (var user in ended)
        {
            var from = user.Plan;
            user.EndPrepaid();
            audit.Add(AuditEntry.PlanChange(PaymentSync.System, user, from, now));
        }
        if (ended.Count > 0) await uow.SaveChangesAsync(ct);
        return ended.Count;
    }
}
