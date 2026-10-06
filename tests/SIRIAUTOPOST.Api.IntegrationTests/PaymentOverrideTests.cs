using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The admin's payment test switch: the listed customers are charged the test amount instead of the plan's price.
[Collection(ApiCollection.Name)]
public class PaymentOverrideTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static async Task<HttpResponseMessage> SetAsync(HttpClient admin, bool enabled, int amount, params string[] emails) =>
        await admin.PutAsJsonAsync("/api/admin/payment-override", new { enabled, amount, emails }, Json);

    private static async Task<PaymentOverrideDto> GetAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<PaymentOverrideDto>("/api/admin/payment-override", Json))!;

    private static async Task<List<AuditEntryDto>> AuditAsync(HttpClient admin, Guid? customer = null) =>
        (await admin.GetFromJsonAsync<List<AuditEntryDto>>($"/api/admin/audit?take=200{(customer is { } id ? $"&customerId={id}" : "")}", Json))!;

    [Fact]
    public async Task Only_platform_admins_can_read_or_change_it()
    {
        var (client, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/payment-override")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(client, true, 20, "a@shop.co")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/payment-override")).StatusCode);
    }

    [Fact]
    public async Task What_the_admin_saves_is_read_back_and_logged_without_the_addresses()
    {
        var admin = await factory.AdminAsync();
        try
        {
            var res = await SetAsync(admin, true, 25, "Tester@Shop.co", "tester@shop.co", "second@shop.co");
            res.EnsureSuccessStatusCode();
            var saved = (await res.Content.ReadFromJsonAsync<PaymentOverrideDto>(Json))!;
            Assert.Equal((true, 25, 10, true), (saved.Enabled, saved.Amount, saved.MinAmount, saved.PaymentsConnected));
            Assert.Equal(["tester@shop.co", "second@shop.co"], saved.Emails);
            Assert.NotNull(saved.UpdatedAt);
            Assert.Equal(saved.Emails, (await GetAsync(admin)).Emails);

            var entry = (await AuditAsync(admin)).First(e => e.Action == AuditAction.PaymentOverrideChanged);
            Assert.Equal(("on amount=25 emails=2", ApiFactory.AdminEmail), (entry.To, entry.ActorEmail));
            Assert.Null(entry.CustomerId);
        }
        finally
        {
            await SetAsync(admin, false, 25);
        }
    }

    [Fact]
    public async Task Bad_input_is_refused_and_the_setting_stays_as_it_was()
    {
        var admin = await factory.AdminAsync();
        try
        {
            (await SetAsync(admin, true, 30, "a@shop.co")).EnsureSuccessStatusCode();

            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetAsync(admin, true, 9, "a@shop.co")).StatusCode); // under Stripe's minimum
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetAsync(admin, true, 1_000_001, "a@shop.co")).StatusCode); // absurd
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetAsync(admin, true, 30)).StatusCode); // on for nobody would hit everyone
            Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(admin, true, 30, "not-an-email")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(admin, true, 30, Enumerable.Range(0, 21).Select(i => $"u{i}@shop.co").ToArray())).StatusCode);

            var now = await GetAsync(admin);
            Assert.Equal((true, 30), (now.Enabled, now.Amount));
            Assert.Equal(["a@shop.co"], now.Emails);
        }
        finally
        {
            await SetAsync(admin, false, 30);
        }
    }

    [Fact]
    public async Task A_listed_customer_pays_the_test_amount_at_checkout_and_gets_the_plan_they_chose()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        var (stranger, strangerAuth, _) = await factory.SignUpAsync();
        try
        {
            (await SetAsync(admin, true, 20, auth.User.Email)).EnsureSuccessStatusCode();

            // The listed customer: the session carries the test amount, the plan is still Pro, yearly.
            (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "pro", cycle = "year" }, Json)).EnsureSuccessStatusCode();
            var session = factory.Payments.Session(factory.Payments.LastSessionOf(auth.User.Id));
            Assert.Equal((PlanKey.Pro, BillingCycle.Year, 790, 20), (session.Plan, session.Cycle, session.Price, session.ChargeOverride));

            // Somebody else keeps the real price.
            (await stranger.PutAsJsonAsync("/api/billing/plan", new { plan = "pro", cycle = "year" }, Json)).EnsureSuccessStatusCode();
            Assert.Null(factory.Payments.Session(factory.Payments.LastSessionOf(strangerAuth.User.Id)).ChargeOverride);

            // The ledger is what Stripe charged: 20 baht, on the Pro plan.
            await factory.SubscribeAsync(client, auth, PlanKey.Pro, BillingCycle.Year, firstInvoice: 20m);
            var charge = Assert.Single((await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!);
            Assert.Equal((20m, PlanKey.Pro), (charge.Amount, charge.Plan));
            Assert.Equal(PlanKey.Pro, (await client.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!.Plan);

            // Every checkout that used it is on the customer's own activity log (two here: the first one above, then the one that was paid), and only theirs.
            var used = (await AuditAsync(admin, auth.User.Id)).Where(e => e.Action == AuditAction.PaymentOverrideUsed).ToList();
            Assert.Equal(2, used.Count);
            Assert.All(used, e => Assert.Equal(("pro", "20", auth.User.Id), (e.From, e.To, e.ActorId)));
            Assert.DoesNotContain(await AuditAsync(admin, strangerAuth.User.Id), e => e.Action == AuditAction.PaymentOverrideUsed);
        }
        finally
        {
            await SetAsync(admin, false, 20);
        }
    }

    [Fact]
    public async Task Switching_it_off_brings_the_plan_price_back_and_a_promo_code_cannot_go_with_it()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        try
        {
            (await SetAsync(admin, true, 15, auth.User.Email)).EnsureSuccessStatusCode();
            var promo = "TST" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
            (await admin.PostAsJsonAsync("/api/admin/promos", new { code = promo, discount = "d20" }, Json)).EnsureSuccessStatusCode();

            // A discount on top of a replaced price makes no sense (and could drop under Stripe's minimum).
            var refused = await client.PutAsJsonAsync("/api/billing/plan", new { plan = "basic", promoCode = promo }, Json);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            (await SetAsync(admin, false, 15, auth.User.Email)).EnsureSuccessStatusCode();
            (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "basic", promoCode = promo }, Json)).EnsureSuccessStatusCode();
            var session = factory.Payments.Session(factory.Payments.LastSessionOf(auth.User.Id));
            Assert.Null(session.ChargeOverride);
            Assert.Equal(58, session.FirstDiscount); // 20% of 290
        }
        finally
        {
            await SetAsync(admin, false, 15);
        }
    }

    [Fact]
    public async Task A_plan_change_with_a_subscription_is_charged_the_test_amount_too()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        try
        {
            var sub = await factory.SubscribeAsync(client, auth, PlanKey.Basic); // paid at the real price
            (await SetAsync(admin, true, 12, auth.User.Email)).EnsureSuccessStatusCode();

            (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "agency" }, Json)).EnsureSuccessStatusCode();
            Assert.Contains(factory.Payments.Changes, c => c == (sub, PlanKey.Agency, BillingCycle.Month, 1990, 12));
            Assert.Equal(PlanKey.Agency, (await client.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!.Plan);

            // Choosing the plan they are on again moves no money, so nothing is overridden or logged a second time.
            (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "agency" }, Json)).EnsureSuccessStatusCode();
            Assert.Single(await AuditAsync(admin, auth.User.Id), e => e.Action == AuditAction.PaymentOverrideUsed);
        }
        finally
        {
            await SetAsync(admin, false, 12);
        }
    }
}
