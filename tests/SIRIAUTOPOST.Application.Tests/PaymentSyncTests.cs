using NSubstitute;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Tests;

public class PaymentSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 3, 30, 0, TimeSpan.Zero);
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ITransactionRepository _transactions = Substitute.For<ITransactionRepository>();
    private readonly IPromoRepository _promos = Substitute.For<IPromoRepository>();
    private readonly IPaymentEventRepository _processed = Substitute.For<IPaymentEventRepository>();
    private readonly IAuditRepository _audit = Substitute.For<IAuditRepository>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly PaymentSync _sync;
    private readonly User _user = User.Create("a@shop.co", "", UserRole.User, PlanKey.Free, Now);

    public PaymentSyncTests()
    {
        _sync = new PaymentSync(_users, _transactions, _promos, _processed, _audit, _gateway, new FixedClock(Now));
        _user.LinkStripeCustomer("cus_1");
        _users.GetByStripeCustomerAsync("cus_1", Arg.Any<CancellationToken>()).Returns(_user);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
    }

    private static SubscriptionSnapshot Sub(string id, SubscriptionState state, PlanKey? plan = PlanKey.Pro, bool cancel = false) =>
        new(id, "cus_1", state, plan, BillingCycle.Month, Now.AddDays(30), cancel);

    private static InvoiceSnapshot Invoice(string id, decimal amount, bool first = false, string sub = "sub_1") =>
        new(id, "cus_1", sub, amount, Now, "https://invoice/" + id, first, PlanKey.Pro, BillingCycle.Month, null);

    [Fact]
    public void A_live_subscription_sets_the_plan_and_writes_the_plan_change()
    {
        _sync.ApplySubscription(_user, Sub("sub_1", SubscriptionState.Active), PaymentSync.System);

        Assert.Equal((PlanKey.Pro, "sub_1", CustomerStatus.Active), (_user.Plan, _user.StripeSubscriptionId, _user.Status));
        _audit.Received(1).Add(Arg.Is<AuditEntry>(e =>
            e.Action == AuditAction.PlanChanged && e.From == "free" && e.To == "pro" && e.ActorId == Guid.Empty && e.CustomerId == _user.Id));
    }

    [Fact]
    public void An_unfinished_or_foreign_subscription_changes_nothing()
    {
        _sync.ApplySubscription(_user, Sub("sub_1", SubscriptionState.Incomplete), PaymentSync.System);
        _sync.ApplySubscription(_user, Sub("sub_2", SubscriptionState.Active, plan: null), PaymentSync.System); // no plan in its metadata

        Assert.Equal((PlanKey.Free, (string?)null), (_user.Plan, _user.StripeSubscriptionId));
        _audit.DidNotReceive().Add(Arg.Any<AuditEntry>());
    }

    [Fact]
    public void Only_the_current_subscription_ending_sends_the_customer_back_to_free()
    {
        _sync.ApplySubscription(_user, Sub("sub_new", SubscriptionState.Active), PaymentSync.System);

        _sync.ApplySubscription(_user, Sub("sub_old", SubscriptionState.Canceled), PaymentSync.System); // a late event of the previous one
        Assert.Equal((PlanKey.Pro, "sub_new"), (_user.Plan, _user.StripeSubscriptionId));

        _sync.ApplySubscription(_user, Sub("sub_new", SubscriptionState.Canceled), PaymentSync.System);
        Assert.Equal((PlanKey.Free, (string?)null), (_user.Plan, _user.StripeSubscriptionId));
    }

    [Fact]
    public void A_canceled_event_does_not_touch_a_plan_the_admin_granted()
    {
        _user.SetPlanByAdmin(PlanKey.Agency);
        _sync.ApplySubscription(_user, Sub("sub_x", SubscriptionState.Canceled), PaymentSync.System);
        Assert.Equal(PlanKey.Agency, _user.Plan);
    }

    [Fact]
    public void Past_due_keeps_the_plan_and_a_scheduled_cancellation_is_remembered()
    {
        _sync.ApplySubscription(_user, Sub("sub_1", SubscriptionState.PastDue, cancel: true), PaymentSync.System);
        Assert.Equal((PlanKey.Pro, CustomerStatus.PastDue, true), (_user.Plan, _user.Status, _user.CancelAtPeriodEnd));
    }

    [Fact]
    public async Task Checkout_links_the_customer_starts_the_subscription_counts_the_promo_and_runs_once()
    {
        var other = User.Create("b@shop.co", "", UserRole.User, PlanKey.Free, Now);
        _users.GetByIdAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        var promo = Promo.Create("LAUNCH20", "d20", Now.AddDays(10));
        _promos.GetByCodeAsync("LAUNCH20", Arg.Any<CancellationToken>()).Returns(promo);
        _gateway.GetSubscriptionAsync("sub_9", Arg.Any<CancellationToken>()).Returns(Sub("sub_9", SubscriptionState.Active, PlanKey.Basic));
        var session = new CheckoutSessionSnapshot("cs_1", "cus_9", "sub_9", true, other.Id, "launch20");

        await _sync.CompleteCheckoutAsync(session, other.Id, default);

        Assert.Equal(("cus_9", PlanKey.Basic, "sub_9"), (other.StripeCustomerId, other.Plan, other.StripeSubscriptionId));
        Assert.Equal(1, promo.Uses);
        _processed.Received(1).Add(Arg.Is<ProcessedPaymentEvent>(p => p.Key == "checkout:cs_1"));

        _processed.ExistsAsync("checkout:cs_1", Arg.Any<CancellationToken>()).Returns(true); // the webhook after the return, or the reverse
        await _sync.CompleteCheckoutAsync(session, other.Id, default);
        Assert.Equal(1, promo.Uses);
        _processed.Received(1).Add(Arg.Any<ProcessedPaymentEvent>());
    }

    [Fact]
    public async Task A_paid_invoice_becomes_one_charge_with_its_payment_and_receipt()
    {
        _gateway.GetInvoicePaymentIntentAsync("in_1", Arg.Any<CancellationToken>()).Returns("pi_1");

        await _sync.RecordInvoicePaidAsync(Invoice("in_1", 790), default);

        _transactions.Received(1).Add(Arg.Is<Transaction>(t =>
            t.Type == TransactionType.Charge && t.Amount == 790m && t.StripeInvoiceId == "in_1" && t.StripePaymentIntentId == "pi_1" &&
            t.ReceiptUrl == "https://invoice/in_1" && t.UserId == _user.Id && t.CanRefund));
    }

    [Fact]
    public async Task An_invoice_that_is_already_in_the_ledger_or_moved_no_money_is_skipped()
    {
        _transactions.GetByInvoiceAsync("in_1", Arg.Any<CancellationToken>())
            .Returns(Transaction.Charge(_user.Id, 790, PlanKey.Pro, BillingCycle.Month, null, Now, new PaymentRefs("in_1")));

        await _sync.RecordInvoicePaidAsync(Invoice("in_1", 790), default);
        await _sync.RecordInvoicePaidAsync(Invoice("in_free", 0), default); // a free first month

        _transactions.DidNotReceive().Add(Arg.Any<Transaction>());
    }

    [Fact]
    public async Task A_failed_renewal_is_recorded_once_and_makes_the_customer_past_due_until_it_is_paid()
    {
        _sync.ApplySubscription(_user, Sub("sub_1", SubscriptionState.Active), PaymentSync.System);

        await _sync.RecordInvoiceFailedAsync(Invoice("in_2", 790), default);

        var failed = (Transaction)_transactions.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(ITransactionRepository.Add)).GetArguments()[0]!;
        Assert.Equal((TransactionType.Failed, "in_2"), (failed.Type, failed.StripeInvoiceId));
        Assert.Equal(CustomerStatus.PastDue, _user.Status);

        // Stripe retries and collects: the same ledger row turns into the paid charge.
        _transactions.GetByInvoiceAsync("in_2", Arg.Any<CancellationToken>()).Returns(failed);
        _gateway.GetInvoicePaymentIntentAsync("in_2", Arg.Any<CancellationToken>()).Returns("pi_2");
        await _sync.RecordInvoicePaidAsync(Invoice("in_2", 790), default);

        Assert.Equal((TransactionType.Charge, "pi_2"), (failed.Type, failed.StripePaymentIntentId));
        Assert.Equal(CustomerStatus.Active, _user.Status);
        _transactions.Received(1).Add(Arg.Any<Transaction>()); // still only the one row
    }

    [Fact]
    public async Task The_failed_first_payment_of_a_checkout_is_not_a_failed_charge()
    {
        await _sync.RecordInvoiceFailedAsync(Invoice("in_first", 790, first: true, sub: "sub_pending"), default);

        _transactions.DidNotReceive().Add(Arg.Any<Transaction>());
        Assert.Equal(CustomerStatus.Active, _user.Status);
    }

    [Fact]
    public async Task A_refund_made_at_stripe_is_recorded_once_against_its_charge()
    {
        var charge = Transaction.Charge(_user.Id, 790, PlanKey.Pro, BillingCycle.Month, null, Now, new PaymentRefs("in_1", "pi_1"));
        _transactions.GetChargeByPaymentIntentAsync("pi_1", Arg.Any<CancellationToken>()).Returns(charge);
        var refund = new RefundSnapshot("re_1", "pi_1", 200, true, Now);

        await _sync.RecordRefundAsync(refund, default);

        _transactions.Received(1).Add(Arg.Is<Transaction>(t =>
            t.Type == TransactionType.Refund && t.Amount == 200m && t.RefundOfId == charge.Id && t.StripeRefundId == "re_1"));
        _audit.Received(1).Add(Arg.Is<AuditEntry>(e => e.Action == AuditAction.Refunded && e.To == "200" && e.ActorId == Guid.Empty));

        _transactions.GetByRefundAsync("re_1", Arg.Any<CancellationToken>()).Returns(Transaction.Charge(_user.Id, 1, PlanKey.Pro, BillingCycle.Month, null, Now));
        await _sync.RecordRefundAsync(refund, default); // redelivered
        await _sync.RecordRefundAsync(refund with { Id = "re_2", Succeeded = false }, default); // failed refund
        await _sync.RecordRefundAsync(refund with { Id = "re_3", PaymentIntentId = "pi_other" }, default); // not ours
        _transactions.Received(1).Add(Arg.Any<Transaction>());
    }
}
