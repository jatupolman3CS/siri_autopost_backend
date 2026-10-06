using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Api.IntegrationTests.Payments;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The in-app payment window: five ways to pay (card, Apple Pay, Google Pay, Link, PromptPay), the PaymentIntent the
// browser confirms, and the webhook that tells the server pending / paid / failed. Stripe is FakePaymentGateway plus
// signed payment_intent.* events.
[Collection(ApiCollection.Name)]
public class PaymentCheckoutTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static async Task<UserDto> MeAsync(HttpClient c) => (await c.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;

    private static async Task<BillingDto> BillingAsync(HttpClient c) => (await c.GetFromJsonAsync<BillingDto>("/api/billing", Json))!;

    private static async Task<PaymentIntentDto> StartAsync(HttpClient c, string method, string plan = "pro", string cycle = "month", string? promo = null)
    {
        var res = await c.PostAsJsonAsync("/api/billing/payments", new { plan, cycle, promoCode = promo, method }, Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<PaymentIntentDto>(Json))!;
    }

    private static async Task<PaymentStatusDto> ConfirmAsync(HttpClient c, string id)
    {
        var res = await c.PostAsJsonAsync($"/api/billing/payments/{id}/confirm", new { }, Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<PaymentStatusDto>(Json))!;
    }

    private Task IntentEventAsync(string type, string id) => factory.SendAsync(type, StripeEvents.PaymentIntent(id));

    [Fact]
    public async Task The_signed_in_customer_gets_the_publishable_key_and_nobody_else()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var config = (await client.GetFromJsonAsync<PaymentConfigDto>("/api/billing/payment-config", Json))!;
        Assert.Equal((ApiFactory.PublishableKey, "thb"), (config.PublishableKey, config.Currency));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/billing/payment-config")).StatusCode);
    }

    [Theory]
    [InlineData("card", PaymentMethodKind.Card)]
    [InlineData("apple_pay", PaymentMethodKind.ApplePay)]
    [InlineData("google_pay", PaymentMethodKind.GooglePay)]
    [InlineData("link", PaymentMethodKind.Link)]
    public async Task A_card_or_wallet_payment_pays_a_subscription_and_the_plan_starts_when_stripe_says_it_is_paid(string method, PaymentMethodKind paid)
    {
        var (client, auth, _) = await factory.SignUpAsync();

        var intent = await StartAsync(client, method);
        Assert.StartsWith("pi_", intent.Id);
        Assert.Contains("_secret_", intent.ClientSecret);
        Assert.Equal((PaymentFlow.Subscription, 790m, "thb"), (intent.Flow, intent.Amount, intent.Currency));
        var request = Assert.Single(factory.Payments.SubscriptionPayments, r => r.UserId == auth.User.Id);
        Assert.Equal((PlanKey.Pro, BillingCycle.Month, 790, 0, (int?)null), (request.Plan, request.Cycle, request.Price, request.FirstDiscount, request.ChargeOverride));
        Assert.Equal(PlanKey.Free, (await MeAsync(client)).Plan); // nothing is paid yet

        var waiting = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Pending, PlanKey.Free), (waiting.Status, waiting.User.Plan));

        factory.Payments.PayIntent(intent.Id, paid);
        var done = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Succeeded, paid, PlanKey.Pro), (done.Status, done.Method, done.User.Plan));
        var billing = await BillingAsync(client);
        Assert.Equal((PlanKey.Pro, true, true), (billing.Plan, billing.HasSubscription, billing.RenewsAt is not null));

        // A settled payment is final: asking again (the browser, or the webhook later) does not go to Stripe.
        var reads = factory.Payments.IntentReads;
        Assert.Equal(PaymentAttemptState.Succeeded, (await ConfirmAsync(client, intent.Id)).Status);
        await IntentEventAsync("payment_intent.succeeded", intent.Id);
        Assert.Equal(reads, factory.Payments.IntentReads);
        Assert.Equal(PlanKey.Pro, (await MeAsync(client)).Plan);
    }

    [Fact]
    public async Task A_subscription_that_stripe_has_not_switched_on_yet_keeps_the_payment_pending()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var intent = await StartAsync(client, "card");

        factory.Payments.PayIntentSubscriptionLate(intent.Id, PaymentMethodKind.Card);
        var early = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Pending, PlanKey.Free), (early.Status, early.User.Plan));

        factory.Payments.ActivateSubscriptionOf(intent.Id);
        var late = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Succeeded, PlanKey.Pro), (late.Status, late.User.Plan));
    }

    [Fact]
    public async Task PromptPay_pays_one_period_up_front_and_the_webhook_starts_the_plan_without_the_browser()
    {
        var (client, auth, _) = await factory.SignUpAsync();

        var intent = await StartAsync(client, "promptpay", "pro", "year");
        Assert.Equal((PaymentFlow.Prepaid, 7584m), (intent.Flow, intent.Amount)); // 632 a month for a year
        var request = Assert.Single(factory.Payments.PrepaidPayments, r => r.UserId == auth.User.Id);
        Assert.Equal((PlanKey.Pro, BillingCycle.Year, 7584m, auth.User.Email), (request.Plan, request.Cycle, request.Amount, request.Email));

        // The customer scans the QR: the bank is processing, then Stripe says it succeeded. Only webhooks arrive.
        factory.Payments.ProcessIntent(intent.Id, PaymentMethodKind.Promptpay);
        await IntentEventAsync("payment_intent.processing", intent.Id);
        Assert.Equal(PlanKey.Free, (await MeAsync(client)).Plan);
        factory.Payments.PayIntent(intent.Id, PaymentMethodKind.Promptpay);
        await IntentEventAsync("payment_intent.succeeded", intent.Id);

        var me = await MeAsync(client);
        Assert.Equal((PlanKey.Pro, BillingCycle.Year), (me.Plan, me.Cycle));
        var billing = await BillingAsync(client);
        Assert.False(billing.HasSubscription);
        Assert.True(billing.RenewsAt > DateTimeOffset.UtcNow.AddMonths(11));
        Assert.True(billing.RenewsAt < DateTimeOffset.UtcNow.AddMonths(13));
        var charge = Assert.Single((await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!);
        Assert.Equal((TransactionType.Charge, 7584m, PlanKey.Pro, true), (charge.Type, charge.Amount, charge.Plan, charge.Refundable));

        // Redelivered webhooks and the browser asking afterwards change nothing: one charge, one period.
        await IntentEventAsync("payment_intent.succeeded", intent.Id);
        var status = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Succeeded, PaymentMethodKind.Promptpay), (status.Status, status.Method));
        Assert.Single((await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!);
        Assert.Equal(billing.RenewsAt, (await BillingAsync(client)).RenewsAt);
    }

    [Fact]
    public async Task Paying_again_for_a_prepaid_plan_adds_a_period_and_a_prepaid_plan_ends_by_itself()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        var first = await StartAsync(client, "promptpay");
        factory.Payments.PayIntent(first.Id, PaymentMethodKind.Promptpay);
        await ConfirmAsync(client, first.Id);
        var ends = (await BillingAsync(client)).RenewsAt!.Value;

        var second = await StartAsync(client, "promptpay"); // a prepaid customer may renew: no subscription to move
        factory.Payments.PayIntent(second.Id, PaymentMethodKind.Promptpay);
        await ConfirmAsync(client, second.Id);
        var extended = (await BillingAsync(client)).RenewsAt!.Value;
        Assert.True(Math.Abs((extended - ends.AddMonths(1)).TotalSeconds) < 5, $"{ends} + 1 month, got {extended}");
        Assert.Equal(2, (await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!.Count);

        // Choosing Free only lets it run out: the plan stays until its end date.
        (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "free" }, Json)).EnsureSuccessStatusCode();
        Assert.Equal(PlanKey.Pro, (await MeAsync(client)).Plan);

        // After the end date the sweep sends the customer back to Free, once, and writes it in the activity log.
        using (factory.Clock.Advance(TimeSpan.FromDays(70)))
        {
            using var scope = factory.Services.CreateScope();
            var sweep = scope.ServiceProvider.GetRequiredService<ICommandHandler<EndPrepaidPlansCommand, int>>();
            Assert.True(await sweep.HandleAsync(new EndPrepaidPlansCommand()) >= 1);
            Assert.Equal(0, await sweep.HandleAsync(new EndPrepaidPlansCommand()));
        }
        var me = await MeAsync(client);
        Assert.Equal(PlanKey.Free, me.Plan);
        Assert.Null((await BillingAsync(client)).RenewsAt);
        var admin = await factory.AdminAsync();
        var audit = (await admin.GetFromJsonAsync<List<AuditEntryDto>>($"/api/admin/audit?take=50&customerId={auth.User.Id}", Json))!;
        Assert.Contains(audit, a => a is { Action: AuditAction.PlanChanged, From: "pro", To: "free" });
    }

    [Fact]
    public async Task A_declined_payment_is_recorded_as_failed_and_can_still_be_paid_on_the_same_intent()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var intent = await StartAsync(client, "card", "basic");

        factory.Payments.DeclineIntent(intent.Id, "Your card was declined.");
        await IntentEventAsync("payment_intent.payment_failed", intent.Id);
        var failed = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Failed, "Your card was declined.", PlanKey.Free), (failed.Status, failed.FailureMessage, failed.User.Plan));

        // The customer tries another card on the same PaymentIntent.
        factory.Payments.PayIntent(intent.Id, PaymentMethodKind.Card);
        await IntentEventAsync("payment_intent.succeeded", intent.Id);
        var paid = await ConfirmAsync(client, intent.Id);
        Assert.Equal((PaymentAttemptState.Succeeded, null, PlanKey.Basic), (paid.Status, paid.FailureMessage, paid.User.Plan));

        // A late "failed" event cannot undo it.
        factory.Payments.DeclineIntent(intent.Id, "late");
        await IntentEventAsync("payment_intent.payment_failed", intent.Id);
        Assert.Equal(PaymentAttemptState.Succeeded, (await ConfirmAsync(client, intent.Id)).Status);
        Assert.Equal(PlanKey.Basic, (await MeAsync(client)).Plan);
    }

    [Fact]
    public async Task A_payment_belongs_to_the_customer_who_started_it_and_stripe_events_of_other_payments_are_ignored()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var (other, _, _) = await factory.SignUpAsync();
        var intent = await StartAsync(client, "promptpay");

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/billing/payments/{intent.Id}/confirm", new { }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/billing/payments/pi_unknown/confirm", new { }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/billing/payments/nonsense/confirm", new { }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"/api/billing/payments/{intent.Id}/confirm", new { }, Json)).StatusCode);

        // A renewal's PaymentIntent (not made by the checkout) is none of this code's business: no Stripe call, no error.
        var reads = factory.Payments.IntentReads;
        await IntentEventAsync("payment_intent.succeeded", "pi_renewal_of_somebody");
        Assert.Equal(reads, factory.Payments.IntentReads);

        // An unsigned or wrongly signed delivery is refused like any webhook.
        var json = StripeEvents.Event("payment_intent.succeeded", StripeEvents.PaymentIntent(intent.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.WebhookAsync(json, secret: "whsec_wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.WebhookAsync(json, secret: null)).StatusCode);
        Assert.Equal(PlanKey.Free, (await MeAsync(client)).Plan);
    }

    [Fact]
    public async Task What_cannot_be_paid_in_the_window_is_refused_with_a_reason()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/billing/payments", new { plan = "free", method = "card" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/billing/payments", new { plan = "pro", method = "bitcoin" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/billing/payments", new { plan = "pro", method = "card" }, Json)).StatusCode);

        // A customer who already has a subscription changes plan with the plan button, not by paying again.
        await factory.SubscribeAsync(client, auth, PlanKey.Basic);
        var again = await client.PostAsJsonAsync("/api/billing/payments", new { plan = "pro", method = "promptpay" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);

        factory.Payments.Enabled = false;
        try
        {
            var (fresh, _, _) = await factory.SignUpAsync();
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fresh.PostAsJsonAsync("/api/billing/payments", new { plan = "pro", method = "card" }, Json)).StatusCode);
        }
        finally
        {
            factory.Payments.Enabled = true;
        }
    }

    // --- Stripe's own page, when the window has no publishable key: the way to pay picked in the window still decides the page ---

    private static async Task<PlanChangeDto> ChoosePlanAsync(HttpClient c, object body)
    {
        var res = await c.PutAsJsonAsync("/api/billing/plan", body, Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<PlanChangeDto>(Json))!;
    }

    [Fact]
    public async Task Picking_PromptPay_for_Stripes_own_page_makes_a_one_off_page_for_one_period_that_the_return_and_the_webhook_apply_once()
    {
        var (client, auth, _) = await factory.SignUpAsync();

        var change = await ChoosePlanAsync(client, new { plan = "pro", cycle = "year", method = "promptpay" });
        Assert.StartsWith("https://checkout.stripe.test/", change.CheckoutUrl);
        Assert.Equal(PlanKey.Free, change.User.Plan); // nothing is paid yet
        var session = factory.Payments.LastSessionOf(auth.User.Id);
        var request = factory.Payments.Session(session);
        Assert.Equal((7584, (int?)null, PlanKey.Pro, BillingCycle.Year), (request.PrepaidAmount, request.ChargeOverride, request.Plan, request.Cycle));

        var intent = factory.Payments.PayCheckout(session);
        var customer = factory.Payments.CustomerOf(auth.User.Id);
        var confirmed = await client.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = session }, Json);
        confirmed.EnsureSuccessStatusCode();
        Assert.Equal((PlanKey.Pro, BillingCycle.Year), ((await confirmed.Content.ReadFromJsonAsync<UserDto>(Json))!.Plan, (await MeAsync(client)).Cycle));

        // Stripe's own webhook for the same session, and the browser coming back again, change nothing.
        await factory.SendAsync("checkout.session.completed",
            StripeEvents.Session(session, customer, null, auth.User.Id, "pro", "year", null, "payment", intent, 7584));
        (await client.PostAsJsonAsync("/api/billing/checkout/confirm", new { sessionId = session }, Json)).EnsureSuccessStatusCode();

        var billing = await BillingAsync(client);
        Assert.False(billing.HasSubscription);
        Assert.True(billing.RenewsAt > DateTimeOffset.UtcNow.AddMonths(11) && billing.RenewsAt < DateTimeOffset.UtcNow.AddMonths(13));
        var charge = Assert.Single((await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!);
        Assert.Equal((TransactionType.Charge, 7584m, true), (charge.Type, charge.Amount, charge.Refundable));
    }

    [Fact]
    public async Task The_webhook_alone_can_apply_a_one_off_page_and_every_other_way_to_pay_gets_the_subscription_page()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        await ChoosePlanAsync(client, new { plan = "basic", method = "promptpay" });
        var session = factory.Payments.LastSessionOf(auth.User.Id);
        var intent = factory.Payments.PayCheckout(session);
        await factory.SendAsync("checkout.session.completed",
            StripeEvents.Session(session, factory.Payments.CustomerOf(auth.User.Id), null, auth.User.Id, "basic", "month", null, "payment", intent, 290));
        Assert.Equal(PlanKey.Basic, (await MeAsync(client)).Plan);

        foreach (var method in new[] { "card", "apple_pay", "google_pay", "link" })
        {
            var (other, otherAuth, _) = await factory.SignUpAsync();
            await ChoosePlanAsync(other, new { plan = "pro", method });
            Assert.Null(factory.Payments.Session(factory.Payments.LastSessionOf(otherAuth.User.Id)).PrepaidAmount);
        }
    }

    [Fact]
    public async Task A_promo_code_or_the_test_amount_sets_the_price_of_the_one_off_page_and_a_subscriber_cannot_use_it()
    {
        var admin = await factory.AdminAsync();
        var code = ("PP" + Guid.NewGuid().ToString("N")[..8]).ToUpperInvariant();
        (await admin.PostAsJsonAsync("/api/admin/promos", new { code, discount = "d20" }, Json)).EnsureSuccessStatusCode();
        var (client, auth, _) = await factory.SignUpAsync();
        await ChoosePlanAsync(client, new { plan = "pro", method = "promptpay", promoCode = code });
        Assert.Equal(632, factory.Payments.Session(factory.Payments.LastSessionOf(auth.User.Id)).PrepaidAmount);

        var (tester, testerAuth, _) = await factory.SignUpAsync();
        (await admin.PutAsJsonAsync("/api/admin/payment-override", new { enabled = true, amount = 25, emails = new[] { testerAuth.User.Email } }, Json)).EnsureSuccessStatusCode();
        try
        {
            await ChoosePlanAsync(tester, new { plan = "pro", method = "promptpay" });
            Assert.Equal(25, factory.Payments.Session(factory.Payments.LastSessionOf(testerAuth.User.Id)).PrepaidAmount);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/admin/payment-override", new { enabled = false, amount = 10, emails = Array.Empty<string>() }, Json)).EnsureSuccessStatusCode();
        }

        var (subscriber, subscriberAuth, _) = await factory.SignUpAsync();
        await factory.SubscribeAsync(subscriber, subscriberAuth, PlanKey.Basic);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await subscriber.PutAsJsonAsync("/api/billing/plan", new { plan = "pro", method = "promptpay" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "pro", method = "bitcoin" }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_promo_code_takes_its_discount_off_the_first_payment_and_is_counted_when_it_is_paid()
    {
        var admin = await factory.AdminAsync();
        var code = ("IN" + Guid.NewGuid().ToString("N")[..8]).ToUpperInvariant();
        (await admin.PostAsJsonAsync("/api/admin/promos", new { code, discount = "d20" }, Json)).EnsureSuccessStatusCode();
        var (client, auth, _) = await factory.SignUpAsync();

        var card = await StartAsync(client, "card", "pro", "month", code.ToLowerInvariant());
        Assert.Equal(632m, card.Amount); // 20% off 790
        Assert.Equal(158, factory.Payments.SubscriptionPayments.Single(r => r.UserId == auth.User.Id).FirstDiscount);
        var promptpay = await StartAsync(client, "promptpay", "pro", "month", code);
        Assert.Equal(632m, promptpay.Amount);

        var bad = await client.PostAsJsonAsync("/api/billing/payments", new { plan = "pro", method = "card", promoCode = "NOSUCHCODE" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        factory.Payments.PayIntent(promptpay.Id, PaymentMethodKind.Promptpay);
        await ConfirmAsync(client, promptpay.Id);
        var uses = (await admin.GetFromJsonAsync<List<PromoDto>>("/api/admin/promos", Json))!.Single(p => p.Code == code).Uses;
        Assert.Equal(1, uses);
    }

    [Fact]
    public async Task The_admins_test_amount_is_what_the_listed_customer_pays_in_the_window()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        var (stranger, _, _) = await factory.SignUpAsync();
        (await admin.PutAsJsonAsync("/api/admin/payment-override", new { enabled = true, amount = 20, emails = new[] { auth.User.Email } }, Json)).EnsureSuccessStatusCode();
        try
        {
            var promptpay = await StartAsync(client, "promptpay");
            Assert.Equal(20m, promptpay.Amount);
            Assert.Equal(20m, factory.Payments.PrepaidPayments.Single(r => r.UserId == auth.User.Id).Amount);
            var card = await StartAsync(client, "card");
            Assert.Equal(20m, card.Amount);
            Assert.Equal(20, factory.Payments.SubscriptionPayments.Single(r => r.UserId == auth.User.Id).ChargeOverride);

            // The test amount replaces the price, so a promo code cannot go with it; and nobody else is touched.
            var withPromo = await client.PostAsJsonAsync("/api/billing/payments", new { plan = "pro", method = "card", promoCode = "ANY" }, Json);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, withPromo.StatusCode);
            Assert.Equal(790m, (await StartAsync(stranger, "promptpay")).Amount);

            var audit = (await admin.GetFromJsonAsync<List<AuditEntryDto>>($"/api/admin/audit?take=50&customerId={auth.User.Id}", Json))!;
            Assert.Contains(audit, a => a.Action == AuditAction.PaymentOverrideUsed);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/admin/payment-override", new { enabled = false, amount = 10, emails = Array.Empty<string>() }, Json)).EnsureSuccessStatusCode();
        }
    }
}
