using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Infrastructure.Payments;

namespace SIRIAUTOPOST.Api.IntegrationTests.Payments;

/// <summary>
/// The real <see cref="StripePaymentGateway"/> with Stripe's HTTP client pointed at a stub: checks the exact form
/// fields we send to Stripe's API and how its answers are read. No network and no key involved.
/// </summary>
public class StripePaymentGatewayTests
{
    private sealed record Call(HttpMethod Method, string Path, Dictionary<string, string> Fields, HttpRequestHeaders Headers);

    private sealed class Stub : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];
        public Func<Call, (HttpStatusCode Status, string Json)> Answer { get; set; } = _ => (HttpStatusCode.NotFound, Error("resource_missing", "No such thing"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var fields = new Dictionary<string, string>();
            foreach (var pair in (request.RequestUri!.Query.TrimStart('?') + "&" + (request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct))).Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                fields[Uri.UnescapeDataString(pair[..eq].Replace('+', ' '))] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
            var call = new Call(request.Method, request.RequestUri.AbsolutePath, fields, request.Headers);
            Calls.Add(call);
            var (status, json) = Answer(call);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string Error(string code, string message, string type = "invalid_request_error") =>
        $$$"""{"error":{"type":"{{{type}}}","code":"{{{code}}}","message":"{{{message}}}"}}""";

    private static string List(string url, params string[] items) =>
        $$$"""{"object":"list","url":"{{{url}}}","has_more":false,"data":[{{{string.Join(",", items)}}}]}""";

    private static StripePaymentGateway Gateway(Stub stub, string key = "sk_test_unit", string currency = "thb") =>
        new(Options.Create(new StripeOptions { SecretKey = key, WebhookSecret = "whsec_x", PortalConfigurationId = "bpc_1", Currency = currency }),
            NullLogger<StripePaymentGateway>.Instance, new HttpClient(stub));

    private static string SubscriptionJson(string id = "sub_1", string status = "active", string plan = "pro", string interval = "month", string extra = "") =>
        """
        {"id":"@ID@","object":"subscription","customer":"cus_1","status":"@STATUS@","cancel_at_period_end":false,
         "metadata":{"plan":"@PLAN@","cycle":"@INTERVAL@","user_id":"u1"}@EXTRA@,
         "items":{"object":"list","url":"/v1/subscription_items","has_more":false,"data":[
           {"id":"si_1","object":"subscription_item","current_period_end":1893456000,"current_period_start":1890864000,
            "price":{"id":"price_1","object":"price","recurring":{"interval":"@INTERVAL@","interval_count":1}}}]}}
        """.Replace("@ID@", id).Replace("@STATUS@", status).Replace("@PLAN@", plan).Replace("@INTERVAL@", interval).Replace("@EXTRA@", extra);

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Checkout_sends_an_inline_recurring_price_on_one_product_and_a_promo_as_a_once_coupon()
    {
        var stub = new Stub();
        stub.Answer = c => (c.Method.Method, c.Path) switch
        {
            ("GET", "/v1/products/autopost_pro") => (HttpStatusCode.NotFound, Error("resource_missing", "No such product")),
            ("POST", "/v1/products") => (HttpStatusCode.OK, """{"id":"autopost_pro","object":"product"}"""),
            ("POST", "/v1/coupons") => (HttpStatusCode.OK, """{"id":"cpn_1","object":"coupon"}"""),
            ("POST", "/v1/checkout/sessions") => (HttpStatusCode.OK, """{"id":"cs_test_1","object":"checkout.session","url":"https://checkout.stripe.com/c/pay/cs_test_1"}"""),
            _ => (HttpStatusCode.NotFound, Error("resource_missing", "unexpected " + c.Path)),
        };

        var url = await Gateway(stub).CreateCheckoutAsync(new CheckoutRequest(
            UserId, "cus_1", PlanKey.Pro, BillingCycle.Year, 790, 158, "LAUNCH20",
            "https://app.test/app/billing?checkout=success&session_id={CHECKOUT_SESSION_ID}", "https://app.test/app/billing?checkout=cancel"));

        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_1", url);
        var product = stub.Calls.Single(c => c.Path == "/v1/products" && c.Method == HttpMethod.Post).Fields;
        Assert.Equal(("autopost_pro", "AutoPost Pro"), (product["id"], product["name"]));
        var coupon = stub.Calls.Single(c => c.Path == "/v1/coupons").Fields;
        Assert.Equal(("15800", "thb", "once", "1"), (coupon["amount_off"], coupon["currency"], coupon["duration"], coupon["max_redemptions"]));
        var s = stub.Calls.Single(c => c.Path == "/v1/checkout/sessions").Fields;
        Assert.Equal(("subscription", "cus_1", UserId.ToString()), (s["mode"], s["customer"], s["client_reference_id"]));
        Assert.Equal("https://app.test/app/billing?checkout=success&session_id={CHECKOUT_SESSION_ID}", s["success_url"]);
        Assert.Equal("https://app.test/app/billing?checkout=cancel", s["cancel_url"]);
        Assert.Equal(("autopost_pro", "thb", "year", "1"), (
            s["line_items[0][price_data][product]"], s["line_items[0][price_data][currency]"],
            s["line_items[0][price_data][recurring][interval]"], s["line_items[0][quantity]"]));
        Assert.Equal((632 * 12 * 100).ToString(), s["line_items[0][price_data][unit_amount]"]); // satang, the yearly rate
        Assert.Equal("cpn_1", s["discounts[0][coupon]"]);
        Assert.Equal(("pro", "year", UserId.ToString(), "LAUNCH20"), (
            s["subscription_data[metadata][plan]"], s["subscription_data[metadata][cycle]"],
            s["subscription_data[metadata][user_id]"], s["subscription_data[metadata][promo]"]));
        Assert.Equal("th", s["locale"]);
    }

    [Fact]
    public async Task Without_a_promo_there_is_no_coupon_and_the_product_is_created_once()
    {
        var stub = new Stub();
        stub.Answer = c => (c.Method.Method, c.Path) switch
        {
            ("GET", "/v1/products/autopost_basic") => (HttpStatusCode.OK, """{"id":"autopost_basic","object":"product"}"""),
            ("POST", "/v1/checkout/sessions") => (HttpStatusCode.OK, """{"id":"cs_test_2","object":"checkout.session","url":"https://checkout.stripe.com/c/pay/cs_test_2"}"""),
            _ => (HttpStatusCode.NotFound, Error("resource_missing", "unexpected " + c.Path)),
        };
        var gateway = Gateway(stub);
        var request = new CheckoutRequest(UserId, "cus_1", PlanKey.Basic, BillingCycle.Month, 290, 0, null, "https://a/s", "https://a/c");

        await gateway.CreateCheckoutAsync(request);
        await gateway.CreateCheckoutAsync(request);

        Assert.DoesNotContain(stub.Calls, c => c.Path == "/v1/coupons");
        Assert.DoesNotContain(stub.Calls, c => c.Path == "/v1/products" && c.Method == HttpMethod.Post);
        Assert.Single(stub.Calls, c => c.Path == "/v1/products/autopost_basic"); // looked up once, then remembered
        var s = stub.Calls.First(c => c.Path == "/v1/checkout/sessions").Fields;
        Assert.Equal(("29000", "month"), (s["line_items[0][price_data][unit_amount]"], s["line_items[0][price_data][recurring][interval]"]));
        Assert.False(s.ContainsKey("discounts[0][coupon]"));
        Assert.False(s.ContainsKey("subscription_data[metadata][promo]"));
    }

    [Fact]
    public async Task A_customer_is_created_once_per_user_under_an_idempotency_key()
    {
        var stub = new Stub();
        stub.Answer = _ => (HttpStatusCode.OK, """{"id":"cus_77","object":"customer"}""");
        var user = User.Create("Shop@Test.co", "Shop", UserRole.User, PlanKey.Free, DateTimeOffset.UtcNow);

        var id = await Gateway(stub).EnsureCustomerAsync(user);

        Assert.Equal("cus_77", id);
        var call = stub.Calls.Single();
        Assert.Equal(("shop@test.co", user.Id.ToString()), (call.Fields["email"], call.Fields["metadata[user_id]"]));
        Assert.Equal($"customer:{user.Id}", call.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task Changing_a_subscription_swaps_the_item_prorates_and_invoices_now_and_a_decline_is_a_domain_error()
    {
        var stub = new Stub();
        stub.Answer = c => (c.Method.Method, c.Path) switch
        {
            ("GET", "/v1/subscriptions/sub_1") => (HttpStatusCode.OK, SubscriptionJson()),
            ("GET", "/v1/products/autopost_agency") => (HttpStatusCode.OK, """{"id":"autopost_agency","object":"product"}"""),
            ("POST", "/v1/subscriptions/sub_1") => (HttpStatusCode.OK, SubscriptionJson(plan: "agency", interval: "year")),
            _ => (HttpStatusCode.NotFound, Error("resource_missing", "unexpected " + c.Path)),
        };
        var gateway = Gateway(stub);

        var snapshot = await gateway.ChangeSubscriptionAsync("sub_1", PlanKey.Agency, BillingCycle.Year, 1990);

        Assert.Equal((PlanKey.Agency, BillingCycle.Year, SubscriptionState.Active), (snapshot.Plan, snapshot.Cycle, snapshot.State));
        var f = stub.Calls.Single(c => c.Method == HttpMethod.Post && c.Path == "/v1/subscriptions/sub_1").Fields;
        Assert.Equal(("si_1", "autopost_agency", "thb", "year", (1592 * 12 * 100).ToString()), (
            f["items[0][id]"], f["items[0][price_data][product]"], f["items[0][price_data][currency]"],
            f["items[0][price_data][recurring][interval]"], f["items[0][price_data][unit_amount]"]));
        Assert.Equal(("always_invoice", "error_if_incomplete", "false"), (f["proration_behavior"], f["payment_behavior"], f["cancel_at_period_end"]));
        Assert.Equal(("agency", "year"), (f["metadata[plan]"], f["metadata[cycle]"]));
        Assert.False(f.ContainsKey("metadata[user_id]")); // the user id set at checkout is left alone

        stub.Answer = c => c.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, SubscriptionJson())
            : (HttpStatusCode.PaymentRequired, Error("card_declined", "Your card was declined.", "card_error"));
        var ex = await Assert.ThrowsAsync<DomainException>(() => gateway.ChangeSubscriptionAsync("sub_1", PlanKey.Agency, BillingCycle.Year, 1990));
        Assert.Contains("Your card was declined.", ex.Message);
    }

    [Fact]
    public async Task Cancelling_resuming_and_pausing_send_what_stripe_expects()
    {
        var stub = new Stub();
        stub.Answer = c => (HttpStatusCode.OK, SubscriptionJson());
        var gateway = Gateway(stub);

        await gateway.SetCancelAtPeriodEndAsync("sub_1", true);
        await gateway.SetCancelAtPeriodEndAsync("sub_1", false);
        await gateway.SetBillingPausedAsync("sub_1", true);
        await gateway.SetBillingPausedAsync("sub_1", false);

        var posts = stub.Calls.Where(c => c.Method == HttpMethod.Post).Select(c => c.Fields).ToList();
        Assert.Equal("true", posts[0]["cancel_at_period_end"]);
        Assert.False(posts[0].ContainsKey("cancel_at"));
        Assert.Equal(("false", ""), (posts[1]["cancel_at_period_end"], posts[1]["cancel_at"])); // clears a date set in the portal too
        Assert.Equal("void", posts[2]["pause_collection[behavior]"]);
        Assert.Equal("", posts[3]["pause_collection"]); // an empty value removes the pause
    }

    [Fact]
    public async Task Subscriptions_are_read_with_plan_cycle_and_the_end_of_the_period()
    {
        var stub = new Stub();
        var gateway = Gateway(stub);

        stub.Answer = _ => (HttpStatusCode.OK, SubscriptionJson(plan: "pro", interval: "month"));
        var live = await gateway.GetSubscriptionAsync("sub_1");
        Assert.Equal(("sub_1", "cus_1", SubscriptionState.Active, PlanKey.Pro, BillingCycle.Month, false), (live.Id, live.CustomerId, live.State, live.Plan, live.Cycle, live.CancelAtPeriodEnd));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), live.RenewsAt); // the item's period end (API 2025+)

        stub.Answer = _ => (HttpStatusCode.OK, SubscriptionJson(status: "past_due"));
        Assert.Equal(SubscriptionState.PastDue, (await gateway.GetSubscriptionAsync("sub_1")).State);
        foreach (var (status, state) in new[] { ("canceled", SubscriptionState.Canceled), ("unpaid", SubscriptionState.Canceled), ("incomplete", SubscriptionState.Incomplete), ("trialing", SubscriptionState.Active) })
        {
            stub.Answer = _ => (HttpStatusCode.OK, SubscriptionJson(status: status));
            Assert.Equal(state, (await gateway.GetSubscriptionAsync("sub_1")).State);
        }

        // A date set in the portal ("cancel at the end of the period") counts as a scheduled cancellation and is the end date.
        stub.Answer = _ => (HttpStatusCode.OK, SubscriptionJson(extra: ",\"cancel_at\":1894000000"));
        var scheduled = await gateway.GetSubscriptionAsync("sub_1");
        Assert.True(scheduled.CancelAtPeriodEnd);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1894000000), scheduled.RenewsAt);

        // Not one of ours (no plan in the metadata): the plan is unknown, the cycle falls back to the price's interval.
        stub.Answer = _ => (HttpStatusCode.OK, SubscriptionJson().Replace("\"plan\":\"pro\",", "").Replace("\"interval\":\"month\"", "\"interval\":\"year\"").Replace("\"cycle\":\"month\"", "\"cycle\":\"year\""));
        var foreign = await gateway.GetSubscriptionAsync("sub_1");
        Assert.Equal((null, BillingCycle.Year), (foreign.Plan, foreign.Cycle));
    }

    [Fact]
    public async Task A_paid_session_is_complete_and_paid_and_belongs_to_the_user_it_was_made_for()
    {
        var stub = new Stub();
        var gateway = Gateway(stub);
        string Session(string status, string payment) =>
            $$$"""{"id":"cs_test_9","object":"checkout.session","mode":"subscription","status":"{{{status}}}","payment_status":"{{{payment}}}","customer":"cus_1","subscription":"sub_1","client_reference_id":"{{{UserId}}}","metadata":{"promo":"LAUNCH20"}}""";

        stub.Answer = _ => (HttpStatusCode.OK, Session("complete", "paid"));
        var paid = await gateway.GetCheckoutSessionAsync("cs_test_9");
        Assert.Equal(("cs_test_9", "cus_1", "sub_1", true, UserId, "LAUNCH20"), (paid.Id, paid.CustomerId, paid.SubscriptionId, paid.Paid, paid.UserId, paid.PromoCode));

        stub.Answer = _ => (HttpStatusCode.OK, Session("complete", "no_payment_required"));
        Assert.True((await gateway.GetCheckoutSessionAsync("cs_test_9")).Paid); // a free first month
        stub.Answer = _ => (HttpStatusCode.OK, Session("open", "unpaid"));
        Assert.False((await gateway.GetCheckoutSessionAsync("cs_test_9")).Paid);
    }

    [Fact]
    public async Task Refunds_go_against_the_payment_intent_in_satang_with_an_idempotency_key()
    {
        var stub = new Stub();
        stub.Answer = _ => (HttpStatusCode.OK, """{"id":"re_1","object":"refund","amount":25753,"payment_intent":"pi_1","status":"succeeded","created":1790000000}""");

        var refund = await Gateway(stub).RefundAsync("pi_1", 257.53m, "refund:abc");

        Assert.Equal(("re_1", "pi_1", 257.53m, true), (refund.Id, refund.PaymentIntentId, refund.Amount, refund.Succeeded));
        var call = stub.Calls.Single();
        Assert.Equal(("pi_1", "25753"), (call.Fields["payment_intent"], call.Fields["amount"]));
        Assert.Equal("refund:abc", call.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task The_payment_of_an_invoice_the_card_on_file_and_the_portal_are_read_and_built_right()
    {
        var stub = new Stub();
        stub.Answer = c => c.Path switch
        {
            "/v1/invoice_payments" => (HttpStatusCode.OK, List("/v1/invoice_payments",
                """{"id":"inpay_1","object":"invoice_payment","status":"canceled","payment":{"type":"payment_intent","payment_intent":"pi_old"}}""",
                """{"id":"inpay_2","object":"invoice_payment","status":"paid","payment":{"type":"payment_intent","payment_intent":"pi_paid"}}""")),
            "/v1/customers/cus_1" => (HttpStatusCode.OK, """
                {"id":"cus_1","object":"customer","invoice_settings":{"default_payment_method":{"id":"pm_1","object":"payment_method","type":"card",
                 "card":{"brand":"visa","last4":"4242","exp_month":12,"exp_year":2030}}}}
                """),
            "/v1/billing_portal/sessions" => (HttpStatusCode.OK, """{"id":"bps_1","object":"billing_portal.session","url":"https://billing.stripe.com/p/session/x"}"""),
            _ => (HttpStatusCode.NotFound, Error("resource_missing", "unexpected " + c.Path)),
        };
        var gateway = Gateway(stub);

        Assert.Equal("pi_paid", await gateway.GetInvoicePaymentIntentAsync("in_1"));
        Assert.Equal("in_1", stub.Calls.Single(c => c.Path == "/v1/invoice_payments").Fields["invoice"]);

        var card = await gateway.GetCardAsync("cus_1");
        Assert.Equal(new CardSnapshot("visa", "4242", 12, 2030), card);

        Assert.Equal("https://billing.stripe.com/p/session/x", await gateway.CreatePortalAsync("cus_1", "https://app.test/app/billing"));
        var portal = stub.Calls.Single(c => c.Path == "/v1/billing_portal/sessions").Fields;
        Assert.Equal(("cus_1", "https://app.test/app/billing", "bpc_1", "th"), (portal["customer"], portal["return_url"], portal["configuration"], portal["locale"]));
    }

    [Fact]
    public async Task A_card_that_cannot_be_read_is_just_missing_and_other_stripe_failures_are_gateway_errors()
    {
        var stub = new Stub();
        stub.Answer = _ => (HttpStatusCode.BadRequest, Error("parameter_invalid_empty", "bad"));
        var gateway = Gateway(stub);

        Assert.Null(await gateway.GetCardAsync("cus_1")); // decoration on the billing page: never fatal
        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.GetSubscriptionAsync("sub_1"));
        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.CreatePortalAsync("cus_1", "https://a/b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_empty_currency_setting_means_baht(string currency)
    {
        // An env file with "Stripe__Currency=" must not make Stripe reject every checkout.
        var stub = new Stub();
        stub.Answer = c => (c.Method.Method, c.Path) switch
        {
            ("GET", "/v1/products/autopost_pro") => (HttpStatusCode.OK, """{"id":"autopost_pro","object":"product"}"""),
            ("POST", "/v1/checkout/sessions") => (HttpStatusCode.OK, """{"id":"cs_test_1","object":"checkout.session","url":"https://checkout.stripe.com/c/pay/cs_test_1"}"""),
            _ => (HttpStatusCode.NotFound, Error("resource_missing", "unexpected " + c.Path)),
        };

        await Gateway(stub, currency: currency).CreateCheckoutAsync(new CheckoutRequest(
            UserId, "cus_1", PlanKey.Pro, BillingCycle.Month, 790, 0, null, "https://app.test/ok", "https://app.test/cancel"));

        var s = stub.Calls.Single(c => c.Path == "/v1/checkout/sessions").Fields;
        Assert.Equal("thb", s["line_items[0][price_data][currency]"]);
    }

    [Fact]
    public async Task Without_a_secret_key_nothing_is_called_and_the_gateway_says_it_is_not_ready()
    {
        var stub = new Stub();
        var gateway = Gateway(stub, key: "");

        Assert.False(gateway.Enabled);
        await Assert.ThrowsAsync<DomainException>(() => gateway.GetSubscriptionAsync("sub_1"));
        await Assert.ThrowsAsync<DomainException>(() => gateway.CreatePortalAsync("cus_1", "https://a/b"));
        Assert.Null(await gateway.GetCardAsync("cus_1"));
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public void Webhook_parser_accepts_multiple_comma_separated_secrets()
    {
        var secretA = "whsec_firstSecretKeyForTesting123456789";
        var secretB = "whsec_secondSecretKeyForTesting987654321";
        var parser = new StripeWebhookParser($"{secretA}, {secretB}");

        var payload = StripeEvents.Event("customer.subscription.deleted", StripeEvents.Subscription("sub_1", "cus_1"));

        // Signed with secret A
        var sigA = StripeEvents.Signature(payload, secretA);
        var evtA = parser.Parse(payload, sigA);
        Assert.NotNull(evtA);
        Assert.Equal("sub_1", Assert.IsType<SubscriptionChangedEvent>(evtA).SubscriptionId);

        // Signed with secret B
        var sigB = StripeEvents.Signature(payload, secretB);
        var evtB = parser.Parse(payload, sigB);
        Assert.NotNull(evtB);
        Assert.Equal("sub_1", Assert.IsType<SubscriptionChangedEvent>(evtB).SubscriptionId);

        // Signed with unknown secret fails
        var sigBad = StripeEvents.Signature(payload, "whsec_wrongKey1234567890");
        Assert.Throws<InvalidWebhookException>(() => parser.Parse(payload, sigBad));
    }
}
