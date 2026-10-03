using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ImpersonationAndHealthTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private HttpClient As(string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<List<AuditEntryDto>> AuditAsync(HttpClient admin, Guid customerId) =>
        (await admin.GetFromJsonAsync<List<AuditEntryDto>>($"/api/admin/audit?customerId={customerId}", Json))!;

    [Fact]
    public async Task An_admin_can_see_the_app_as_a_customer_read_only_for_an_hour()
    {
        var admin = await factory.AdminAsync();
        var (_, auth, ws) = await factory.SignUpAsync();
        var res = await admin.PostAsync($"/api/admin/customers/{auth.User.Id}/impersonate", null);
        res.EnsureSuccessStatusCode();
        var imp = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal(auth.User.Id, imp.User.Id);
        Assert.InRange(imp.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(61));

        var client = As(imp.Token);
        var workspaces = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!;
        Assert.Equal(ws, Assert.Single(workspaces).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/customers")).StatusCode); // a customer's session
        // Read-only: nothing is changed in the customer's name.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "pro" }, Json)).StatusCode);
        var post = await client.PostAsJsonAsync($"/api/workspaces/{ws}/snippets", new { label = "x", text = "y" }, Json);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Contains("ดูได้อย่างเดียว", await post.Content.ReadAsStringAsync());

        // The admin looking around does not count as the customer being active.
        var before = (await admin.GetFromJsonAsync<List<CustomerDto>>("/api/admin/customers", Json))!.Single(c => c.Id == auth.User.Id);
        await client.GetAsync("/api/auth/me");
        var after = (await admin.GetFromJsonAsync<List<CustomerDto>>("/api/admin/customers", Json))!.Single(c => c.Id == auth.User.Id);
        Assert.Equal(before.LastActiveAt, after.LastActiveAt);

        var entry = (await AuditAsync(admin, auth.User.Id)).First();
        Assert.Equal((AuditAction.Impersonated, ApiFactory.AdminEmail), (entry.Action, entry.ActorEmail));
    }

    [Fact]
    public async Task Suspended_customers_can_still_be_inspected_but_admins_cannot_be_impersonated()
    {
        var admin = await factory.AdminAsync();
        var (_, auth, _) = await factory.SignUpAsync();
        await admin.PostAsJsonAsync($"/api/admin/customers/{auth.User.Id}/status", new { status = "suspended" }, Json);
        var imp = (await (await admin.PostAsync($"/api/admin/customers/{auth.User.Id}/impersonate", null))
            .Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await As(imp.Token).GetAsync("/api/workspaces")).StatusCode);

        var me = (await admin.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!;
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/customers/{me.Id}/impersonate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync($"/api/admin/customers/{auth.User.Id}/impersonate", null)).StatusCode);
    }

    [Fact]
    public async Task Admin_actions_and_plan_changes_are_logged()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        var id = auth.User.Id;
        // The customer subscribes and Stripe, not a person, applies it; Stripe ends it later the same way.
        var sub = await factory.SubscribeAsync(client, auth, PlanKey.Pro);
        factory.Payments.Set(sub, s => s with { State = Application.Interfaces.SubscriptionState.Canceled });
        await factory.SendAsync("customer.subscription.deleted", Payments.StripeEvents.Subscription(sub, factory.Payments.CustomerOf(id), "canceled"));
        await admin.PutAsJsonAsync($"/api/admin/customers/{id}/plan", new { plan = "agency" }, Json);
        await admin.PutAsJsonAsync($"/api/admin/customers/{id}/limits", new { accounts = 5, posts = (int?)null, devices = 0, seats = (int?)null }, Json);
        await admin.PostAsJsonAsync($"/api/admin/customers/{id}/status", new { status = "suspended" }, Json);
        await admin.PostAsJsonAsync($"/api/admin/customers/{id}/status", new { status = "active" }, Json);
        await admin.PostAsync($"/api/admin/customers/{id}/refund", null);

        var log = await AuditAsync(admin, id);
        Assert.Equal(
            [AuditAction.Refunded, AuditAction.StatusChanged, AuditAction.StatusChanged, AuditAction.LimitsChanged, AuditAction.PlanChanged, AuditAction.PlanChanged, AuditAction.PlanChanged],
            log.Select(e => e.Action));
        Assert.Equal((Guid.Empty, "", "free", "pro"), (log[^1].ActorId, log[^1].ActorEmail, log[^1].From, log[^1].To)); // no person behind it
        Assert.Equal((Guid.Empty, "pro", "free"), (log[^2].ActorId, log[^2].From, log[^2].To));
        Assert.Equal((ApiFactory.AdminEmail, "free", "agency"), (log[^3].ActorEmail, log[^3].From, log[^3].To));
        Assert.Equal("accounts=5 posts=- devices=0 seats=-", log[^4].To);
        Assert.Equal(("active", "suspended"), (log[^5].From, log[^5].To));
        Assert.Equal("790", log[0].To); // the refunded amount
    }

    [Fact]
    public async Task Health_reports_live_mrr_devices_and_latency()
    {
        var admin = await factory.AdminAsync();
        var before = (await admin.GetFromJsonAsync<PlatformHealthDto>("/api/admin/health", Json))!;
        var (client, auth, _) = await factory.SignUpAsync();
        await factory.SubscribeAsync(client, auth, PlanKey.Pro);

        var after = (await admin.GetFromJsonAsync<PlatformHealthDto>("/api/admin/health", Json))!;
        Assert.Equal(before.Mrr + 790, after.Mrr);
        Assert.Equal(before.MrrPrev, after.MrrPrev); // they were not a customer 30 days ago
        Assert.NotNull(after.ApiP95Ms);
        Assert.True(after.ApiSamples > 0);
        Assert.InRange(after.DbMs, 0, 5000);
        Assert.True(after.PaymentsConnected); // Stripe is configured (the fake)
    }
}
