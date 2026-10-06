using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Api.IntegrationTests.Payments;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The payment flow end to end: the web app's calls on one side, Stripe (FakePaymentGateway + signed webhooks) on the other.
[Collection(ApiCollection.Name)]
public class StripeBillingTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static async Task<UserDto> MeAsync(HttpClient c) => (await c.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;

    private static async Task<BillingDto> BillingAsync(HttpClient c) => (await c.GetFromJsonAsync<BillingDto>("/api/billing", Json))!;

    private static async Task<List<TransactionDto>> InvoicesAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!;

    private static async Task<PlanChangeDto> ChoosePlanAsync(HttpClient c, object body)
    {
        var res = await c.PutAsJsonAsync("/api/billing/plan", body, Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<PlanChangeDto>(Json))!;
    }

    [Fact]
    public async Task A_paid_plan_is_bought_at_checkout_and_only_applies_once_stripe_says_so()
    {
        var (client, auth, _) = await factory.SignUpAsync();

        var change = await ChoosePlanAsync(client, new { plan = "pro", cycle = "year" });
        Assert.StartsWith("https://checkout.stripe.test/", change.CheckoutUrl);
        Assert.Equal(PlanKey.Free, change.User.Plan); // nothing is paid yet
        var session = factory.Payments.Session(factory.Payments.LastSessionOf(auth.User.Id));
        Assert.Equal((PlanKey.Pro, BillingCycle.Year, 790, 0), (session.Plan, session.Cycle, session.Price, session.FirstDiscount));
        Assert.EndsWith("/app/billing?checkout=success&session_id={CHECKOUT_SESSION_ID}", session.SuccessUrl);
        Assert.EndsWith("/app/billing?checkout=cancel", session.CancelUrl);

        await factory.SubscribeAsync(client, auth, PlanKey.Pro, BillingCycle.Year);

        var me = await MeAsync(client);
        Assert.Equal((PlanKey.Pro, BillingCycle.Year, CustomerStatus.Active), (me.Plan, me.Cycle, me.Status));
        var billing = await BillingAsync(client);
        Assert.Equal((true, true, false, true), (billing.PaymentsEnabled, billing.HasSubscription, billing.CancelAtPeriodEnd, billing.CanManagePayment));
        Assert.Equal(("visa", "4242"), (billing.Card!.Brand, billing.Card.Last4));
        Assert.NotNull(billing.RenewsAt);
        var charge = Assert.Single(await InvoicesAsync(client));
        Assert.Equal((TransactionType.Charge, 632m * 12, PlanKey.Pro, true), (charge.Type, charge.Amount, charge.Plan, charge.Refundable));
        Assert.StartsWith("https://invoice.stripe.test/i/", charge.ReceiptUrl);
    }

    [Fact]
    public async Task Billing_shows_the_limits_in_force_and_what_is_really_used()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, ws) = await factory.SignUpAsync();

        // A new workspace is empty: nothing is usage yet.
        var billing = await BillingAsync(client);
        Assert.Equal(new LimitsDto(1, 10, 1, 1, 10, 20, 20), billing.Limits);
        Assert.Equal(new UsageDto(0, 0, 0, 0, 0, 0), billing.Usage);

        // The platform admin's per-customer override is what the customer sees, and a paired device counts.
        await admin.PutAsJsonAsync($"/api/admin/customers/{auth.User.Id}/limits", new { devices = 3, posts = 0 }, Json);
        var code = (await (await client.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content.ReadFromJsonAsync<PairingCodeDto>(Json))!.Code;
        (await factory.CreateClient().PostAsJsonAsync("/api/device/pair", new { code, name = "PC" })).EnsureSuccessStatusCode();
        billing = await BillingAsync(client);
        Assert.Equal(new LimitsDto(1, null, 3, 1, 10, 20, 20), billing.Limits); // posts 0 = unlimited
        Assert.Equal(new UsageDto(1, 0, 1, 0, 0, 0), billing.Usage);
    }

    [Fact]
    public async Task Stripe_delivering_the_same_event_twice_changes_nothing_the_second_time()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        await ChoosePlanAsync(client, new { plan = "basic" });
        var session = factory.Payments.LastSessionOf(auth.User.Id);
        var sub = factory.Payments.PayCheckout(session);
        var customer = factory.Payments.CustomerOf(auth.User.Id);
        var invoice = StripeEvents.Event("invoice.paid",
            StripeEvents.Invoice("in_dup_" + Guid.NewGuid().ToString("N")[..8], customer, sub, 290, auth.User.Id, "basic", "month", "subscription_create"));

        for (var i = 0; i < 3; i++)
        {
            (await factory.WebhookAsync(invoice)).EnsureSuccessStatusCode();
            (await factory.WebhookAsync(StripeEvents.Event("checkout.session.completed", StripeEvents.Session(session, customer, sub, auth.User.Id, "basic", "month"), "evt_same_" + session))).EnsureSuccessStatusCode();
        }

        Assert.Single(await InvoicesAsync(client));
        Assert.Equal(PlanKey.Basic, (await MeAsync(client)).Plan);
    }

    [Fact]
    public async Task The_return_from_checkout_applies_the_plan_without_a_webhook_and_belongs_to_the_buyer()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        var (other, _, _) = await factory.SignUpAsync();
        await ChoosePlanAsync(client, new { plan = "pro" });
        var session = factory.Payments.LastSessionOf(auth.User.Id);

        // Back from Stripe before the customer has paid: refused.
        var early = await client.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = session }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);

        factory.Payments.PayCheckout(session);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = session }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = "nonsense" }, Json)).StatusCode);

        var res = await client.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = session }, Json);
        res.EnsureSuccessStatusCode();
        Assert.Equal(PlanKey.Pro, (await res.Content.ReadFromJsonAsync<UserDto>(Json))!.Plan);
        Assert.Equal(PlanKey.Pro, (await MeAsync(client)).Plan);
        (await client.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = session }, Json)).EnsureSuccessStatusCode(); // pressing back and forward is harmless
    }

    [Fact]
    public async Task A_customer_with_a_subscription_moves_between_plans_at_once_and_a_declined_card_stops_it()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        var sub = await factory.SubscribeAsync(client, auth, PlanKey.Basic);

        var up = await ChoosePlanAsync(client, new { plan = "agency", cycle = "year" });
        Assert.Null(up.CheckoutUrl);
        Assert.Equal((PlanKey.Agency, BillingCycle.Year), (up.User.Plan, up.User.Cycle));
        Assert.Contains(factory.Payments.Changes, c => c == (sub, PlanKey.Agency, BillingCycle.Year, 1990, null));

        var bad = await client.PutAsJsonAsync("/api/billing/plan", new { plan = "pro", promoCode = "ANY" }, Json); // a code is for the first invoice only
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        factory.Payments.Decline = "Your card was declined.";
        try
        {
            var declined = await client.PutAsJsonAsync("/api/billing/plan", new { plan = "pro" }, Json);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, declined.StatusCode);
            Assert.Contains("Your card was declined.", await declined.Content.ReadAsStringAsync());
        }
        finally
        {
            factory.Payments.Decline = null;
        }
        Assert.Equal(PlanKey.Agency, (await MeAsync(client)).Plan); // the plan did not move
    }

    [Fact]
    public async Task Going_free_cancels_at_the_end_of_the_period_and_choosing_the_plan_again_takes_it_back()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        var sub = await factory.SubscribeAsync(client, auth, PlanKey.Pro);

        var cancelled = await ChoosePlanAsync(client, new { plan = "free" });
        Assert.Equal(PlanKey.Pro, cancelled.User.Plan); // paid until the period ends
        var billing = await BillingAsync(client);
        Assert.True(billing.CancelAtPeriodEnd);
        Assert.True(billing.HasSubscription);

        await ChoosePlanAsync(client, new { plan = "pro" });
        Assert.False((await BillingAsync(client)).CancelAtPeriodEnd);

        await ChoosePlanAsync(client, new { plan = "free" });
        factory.Payments.Set(sub, s => s with { State = SubscriptionState.Canceled });
        await factory.SendAsync("customer.subscription.deleted", StripeEvents.Subscription(sub, factory.Payments.CustomerOf(auth.User.Id), "canceled"));

        var me = await MeAsync(client);
        Assert.Equal(PlanKey.Free, me.Plan);
        billing = await BillingAsync(client);
        Assert.Equal((false, false), (billing.HasSubscription, billing.CancelAtPeriodEnd));
        Assert.True(billing.CanManagePayment); // the Stripe customer stays: invoices and the card are still reachable
    }

    [Fact]
    public async Task A_failed_renewal_makes_the_customer_past_due_and_the_retry_that_works_clears_it()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        var sub = await factory.SubscribeAsync(client, auth, PlanKey.Pro);
        var customer = factory.Payments.CustomerOf(auth.User.Id);
        var invoiceId = "in_renew_" + Guid.NewGuid().ToString("N")[..8];

        await factory.SendAsync("invoice.payment_failed", StripeEvents.Invoice(invoiceId, customer, sub, 790, auth.User.Id, "pro", "month"));
        Assert.Equal((PlanKey.Pro, CustomerStatus.PastDue), ((await MeAsync(client)).Plan, (await MeAsync(client)).Status));
        var failed = (await InvoicesAsync(client)).Single(t => t.Type == TransactionType.Failed);
        Assert.False(failed.Refundable);

        // The admin asks Stripe to collect it again; with a declining card it fails and nothing changes.
        factory.Payments.OpenInvoices[invoiceId] = new InvoiceSnapshot(
            invoiceId, customer, sub, 790, DateTimeOffset.UtcNow, "https://invoice.stripe.test/i/" + invoiceId, false, PlanKey.Pro, BillingCycle.Month, null);
        factory.Payments.Decline = "insufficient_funds";
        try
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync($"/api/admin/transactions/{failed.Id}/retry", null)).StatusCode);
        }
        finally
        {
            factory.Payments.Decline = null;
        }
        Assert.Equal(CustomerStatus.PastDue, (await MeAsync(client)).Status);

        var paid = (await (await admin.PostAsync($"/api/admin/transactions/{failed.Id}/retry", null)).Content.ReadFromJsonAsync<TransactionDto>(Json))!;
        Assert.Equal((TransactionType.Charge, 790m, true), (paid.Type, paid.Amount, paid.Refundable));
        Assert.Equal(CustomerStatus.Active, (await MeAsync(client)).Status);
        Assert.Contains(await InvoicesAsync(client), t => t.Id == paid.Id && t.Type == TransactionType.Charge);

        // Stripe's own invoice.paid for the same invoice arrives afterwards: still one row.
        await factory.SendAsync("invoice.paid", StripeEvents.Invoice(invoiceId, customer, sub, 790, auth.User.Id, "pro", "month"));
        Assert.Equal(2, (await InvoicesAsync(client)).Count); // the first payment and this renewal
        Assert.DoesNotContain(await InvoicesAsync(client), t => t.Type == TransactionType.Failed);

        // Only failed invoices can be retried.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync($"/api/admin/transactions/{paid.Id}/retry", null)).StatusCode);
    }

    [Fact]
    public async Task The_admin_refunds_through_stripe_once_and_a_refund_made_in_the_stripe_dashboard_is_recorded()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        await factory.SubscribeAsync(client, auth, PlanKey.Basic);
        var charge = Assert.Single(await InvoicesAsync(client));

        var refund = (await (await admin.PostAsync($"/api/admin/transactions/{charge.Id}/refund", null)).Content.ReadFromJsonAsync<TransactionDto>(Json))!;
        Assert.Equal((TransactionType.Refund, 290m), (refund.Type, refund.Amount));
        Assert.Contains(factory.Payments.Refunds, r => r.Key == $"refund:{charge.Id}" && r.Amount == 290m);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/transactions/{charge.Id}/refund", null)).StatusCode);

        // Stripe reports the refund the admin just made: the same Stripe refund is not recorded a second time.
        var intent = factory.Payments.Refunds.Last(r => r.Key == $"refund:{charge.Id}").PaymentIntent;
        var refundId = factory.Payments.RefundIdOf($"refund:{charge.Id}");
        await factory.SendAsync("refund.created", StripeEvents.Refund(refundId, intent, 290));
        await factory.SendAsync("refund.updated", StripeEvents.Refund(refundId, intent, 290));
        Assert.Equal(2, (await InvoicesAsync(client)).Count); // the charge and its one refund
    }

    [Fact]
    public async Task A_refund_made_only_in_the_stripe_dashboard_shows_up_in_the_ledger_and_the_log()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        await factory.SubscribeAsync(client, auth, PlanKey.Pro);
        var charge = Assert.Single(await InvoicesAsync(client));
        var intent = "pi_" + charge.ReceiptUrl!.Split('/').Last(); // the fake pays every invoice with pi_{invoice id}

        await factory.SendAsync("refund.created", StripeEvents.Refund("re_only_" + Guid.NewGuid().ToString("N")[..8], intent, 200));
        await factory.SendAsync("refund.created", StripeEvents.Refund("re_failed_" + Guid.NewGuid().ToString("N")[..8], intent, 100, "failed"));
        await factory.SendAsync("refund.created", StripeEvents.Refund("re_other", "pi_unknown", 50)); // not one of ours

        var rows = await InvoicesAsync(client);
        var refund = Assert.Single(rows, t => t.Type == TransactionType.Refund);
        Assert.Equal(200m, refund.Amount);
        var log = (await admin.GetFromJsonAsync<List<AuditEntryDto>>($"/api/admin/audit?customerId={auth.User.Id}", Json))!;
        Assert.Contains(log, e => e.Action == AuditAction.Refunded && e.To == "200" && e.ActorId == Guid.Empty);
    }

    [Fact]
    public async Task Without_a_signature_or_with_a_wrong_one_the_webhook_is_refused_and_unknown_events_are_ignored()
    {
        var body = StripeEvents.Event("invoice.paid", StripeEvents.Invoice("in_x", "cus_x", "sub_x", 1, Guid.NewGuid(), "pro", "month"));
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.WebhookAsync(body, secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.WebhookAsync(body, "whsec_someone_else")).StatusCode);

        // A body changed after signing no longer verifies.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(body.Replace("in_x", "in_y"), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("Stripe-Signature", StripeEvents.Signature(body, ApiFactory.StripeWebhookSecret));
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().SendAsync(req)).StatusCode);

        // A stale signature (replay) is refused too.
        var old = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        old.Headers.Add("Stripe-Signature", StripeEvents.Signature(body, ApiFactory.StripeWebhookSecret, DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().SendAsync(old)).StatusCode);

        // An event type we do not act on, and an invoice of a customer we do not know, are accepted and do nothing.
        (await factory.WebhookAsync(StripeEvents.Event("customer.created", new Dictionary<string, object?> { ["id"] = "cus_z", ["object"] = "customer" }))).EnsureSuccessStatusCode();
        (await factory.WebhookAsync(body)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Paid_plans_cannot_be_bought_while_stripe_is_not_configured_and_the_free_plan_still_works()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        factory.Payments.Enabled = false;
        try
        {
            var res = await client.PutAsJsonAsync("/api/billing/plan", new { plan = "pro" }, Json);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.Contains("Stripe", await res.Content.ReadAsStringAsync());
            Assert.False((await BillingAsync(client)).PaymentsEnabled);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/billing/portal", null)).StatusCode);
            Assert.Equal(PlanKey.Free, (await ChoosePlanAsync(client, new { plan = "free" })).User.Plan);
        }
        finally
        {
            factory.Payments.Enabled = true;
        }
        Assert.Equal(PlanKey.Free, (await MeAsync(client)).Plan);
        _ = auth;
    }

    [Fact]
    public async Task The_billing_portal_needs_a_stripe_customer_and_the_admin_cannot_move_a_paid_plan()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/billing/portal", null)).StatusCode); // nothing bought yet
        Assert.False((await BillingAsync(client)).CanManagePayment);

        await factory.SubscribeAsync(client, auth, PlanKey.Pro);
        var portal = (await (await client.PostAsync("/api/billing/portal", null)).Content.ReadFromJsonAsync<UrlDto>(Json))!;
        Assert.Contains(factory.Payments.CustomerOf(auth.User.Id), portal.Url);
        Assert.Contains(Uri.EscapeDataString("/app/billing"), portal.Url); // where the customer returns to

        // The customer pays through Stripe, so Stripe decides the plan: the admin's change is refused, not silently undone later.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync($"/api/admin/customers/{auth.User.Id}/plan", new { plan = "agency" }, Json)).StatusCode);
        Assert.Equal(PlanKey.Pro, (await MeAsync(client)).Plan);
        var row = (await admin.GetFromJsonAsync<List<CustomerDto>>("/api/admin/customers", Json))!.Single(c => c.Id == auth.User.Id);
        Assert.Equal((true, false), (row.HasSubscription, row.CancelAtPeriodEnd));
    }

    [Fact]
    public async Task A_suspended_customer_is_not_billed_until_restored()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        var sub = await factory.SubscribeAsync(client, auth, PlanKey.Basic);

        await admin.PostAsJsonAsync($"/api/admin/customers/{auth.User.Id}/status", new { status = "suspended" }, Json);
        await admin.PostAsJsonAsync($"/api/admin/customers/{auth.User.Id}/status", new { status = "banned" }, Json); // still blocked: no second call
        await admin.PostAsJsonAsync($"/api/admin/customers/{auth.User.Id}/status", new { status = "active" }, Json);

        Assert.Equal([(sub, true), (sub, false)], factory.Payments.Pauses.Where(p => p.Subscription == sub).ToArray());

        // Stripe's own events do not lift the block.
        await admin.PostAsJsonAsync($"/api/admin/customers/{auth.User.Id}/status", new { status = "suspended" }, Json);
        await factory.SendAsync("customer.subscription.updated", StripeEvents.Subscription(sub, factory.Payments.CustomerOf(auth.User.Id)));
        var row = (await admin.GetFromJsonAsync<List<CustomerDto>>("/api/admin/customers", Json))!.Single(c => c.Id == auth.User.Id);
        Assert.Equal(CustomerStatus.Suspended, row.Status);
    }
}
