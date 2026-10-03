using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AdminEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private async Task<CustomerDto> CustomerAsync(HttpClient admin, Guid id) =>
        (await admin.GetFromJsonAsync<List<CustomerDto>>("/api/admin/customers", Json))!.Single(c => c.Id == id);

    [Fact]
    public async Task Only_platform_admins_get_in()
    {
        var (client, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/customers")).StatusCode);
    }

    [Fact]
    public async Task Suspending_a_customer_locks_them_out_until_restored()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, _) = await factory.SignUpAsync();
        var c = await CustomerAsync(admin, auth.User.Id);
        Assert.Equal((CustomerStatus.Active, 1, PlanKey.Free), (c.Status, c.Workspaces, c.Plan));
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<CustomerDto>>("/api/admin/customers", Json))!,
            x => x.Email == ApiFactory.AdminEmail);

        var res = await admin.PostAsJsonAsync($"/api/admin/customers/{c.Id}/status", new { status = "suspended" }, Json);
        Assert.Equal((CustomerStatus.Suspended, true), ((await res.Content.ReadFromJsonAsync<CustomerDto>(Json))!.Status, (await CustomerAsync(admin, c.Id)).Paused));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/workspaces")).StatusCode); // the old token too
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.User.Email, password = "password1" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("ระงับ", await login.Content.ReadAsStringAsync());

        await admin.PostAsJsonAsync($"/api/admin/customers/{c.Id}/status", new { status = "active" }, Json);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workspaces")).StatusCode);
    }

    [Fact]
    public async Task Plan_limits_and_overrides_decide_how_many_devices_pair()
    {
        var admin = await factory.AdminAsync();
        var (client, auth, ws) = await factory.SignUpAsync(); // free: 1 device, 1 account
        async Task<HttpStatusCode> Pair()
        {
            var r = await client.PostAsync($"/api/workspaces/{ws}/devices/pairing", null);
            if (!r.IsSuccessStatusCode) return r.StatusCode;
            var code = (await r.Content.ReadFromJsonAsync<PairingCodeDto>(Json))!.Code;
            return (await factory.CreateClient().PostAsJsonAsync("/api/device/pair", new { code, name = "PC" })).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await Pair());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Pair());

        var c = (await (await admin.PutAsJsonAsync($"/api/admin/customers/{auth.User.Id}/limits", new { devices = 2, accounts = 0 }, Json))
            .Content.ReadFromJsonAsync<CustomerDto>(Json))!;
        Assert.Equal((2, 0), (c.Limits.Devices, c.Limits.Accounts));
        Assert.Equal(HttpStatusCode.OK, await Pair());
        c = await CustomerAsync(admin, auth.User.Id);
        Assert.Equal((2, 2), (c.Devices.Count, c.Accounts));

        // Unbinding from the admin page frees a slot.
        await admin.DeleteAsync($"/api/admin/customers/{c.Id}/devices/{c.Devices[0].Id}");
        Assert.Single((await CustomerAsync(admin, c.Id)).Devices);

        // A plan change from the admin page clears the overrides and charges nothing.
        c = (await (await admin.PutAsJsonAsync($"/api/admin/customers/{c.Id}/plan", new { plan = "agency" }, Json))
            .Content.ReadFromJsonAsync<CustomerDto>(Json))!;
        Assert.Equal((PlanKey.Agency, (int?)null), (c.Plan, c.Limits.Devices));
        Assert.Empty((await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!);
    }

    [Fact]
    public async Task Refunds_and_revenue()
    {
        var admin = await factory.AdminAsync();
        var before = (await admin.GetFromJsonAsync<AdminSummaryDto>("/api/admin/summary", Json))!;
        var (client, auth, _) = await factory.SignUpAsync();
        await factory.SubscribeAsync(client, auth, PlanKey.Agency);

        var summary = (await admin.GetFromJsonAsync<AdminSummaryDto>("/api/admin/summary", Json))!;
        Assert.Equal(before.Agency + 1, summary.Agency);
        Assert.Equal(12, summary.Revenue.Count);
        Assert.Equal(before.Revenue[^1].Amount + 1990, summary.Revenue[^1].Amount);

        var refund = (await (await admin.PostAsync($"/api/admin/customers/{auth.User.Id}/refund", null)).Content.ReadFromJsonAsync<TransactionDto>(Json))!;
        Assert.Equal((TransactionType.Refund, 1990m), (refund.Type, refund.Amount));
        Assert.Contains(factory.Payments.Refunds, r => r.Amount == 1990m && r.PaymentIntent.StartsWith("pi_in_")); // the money went back through Stripe
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/customers/{auth.User.Id}/refund", null)).StatusCode);
        var all = (await admin.GetFromJsonAsync<List<TransactionDto>>("/api/admin/transactions", Json))!;
        Assert.Equal(2, all.Count(t => t.UserId == auth.User.Id));
        summary = (await admin.GetFromJsonAsync<AdminSummaryDto>("/api/admin/summary", Json))!;
        Assert.Equal(before.Revenue[^1].Amount, summary.Revenue[^1].Amount);
    }

    [Fact]
    public async Task Plan_prices_are_edited_by_the_admin()
    {
        var admin = await factory.AdminAsync();
        var bad = await admin.PutAsJsonAsync("/api/admin/plans/free", new { price = 99, accounts = 1, posts = 10, devices = 1, seats = 1 }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var plans = (await admin.GetFromJsonAsync<List<PlanDto>>("/api/plans", Json))!;
        var basic = plans.Single(p => p.Key == PlanKey.Basic);
        try
        {
            (await admin.PutAsJsonAsync("/api/admin/plans/basic", new { price = 350, accounts = 3, posts = 40, devices = 1, seats = 1 }, Json))
                .EnsureSuccessStatusCode();
            var now = (await factory.CreateClient().GetFromJsonAsync<List<PlanDto>>("/api/plans", Json))!.Single(p => p.Key == PlanKey.Basic);
            Assert.Equal((350, (int?)40), (now.Price, now.Posts));
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/plans/basic",
                new { price = basic.Price, accounts = basic.Accounts, posts = basic.Posts, devices = basic.Devices, seats = basic.Seats }, Json);
        }
    }

    [Fact]
    public async Task The_plans_daily_posts_hold_back_posts_and_the_jobs_feed_shows_them()
    {
        var admin = await factory.AdminAsync();
        var (owner, auth, ws) = await factory.SignUpAsync();
        await admin.PutAsJsonAsync($"/api/admin/customers/{auth.User.Id}/limits", new { posts = 1 }, Json);
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content.ReadFromJsonAsync<PairingCodeDto>(Json))!.Code;
        var pair = (await (await factory.CreateClient().PostAsJsonAsync("/api/device/pair", new { code, name = "PC" })).Content
            .ReadFromJsonAsync<PairResultDto>(Json))!;
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        await device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "G", url = "https://www.facebook.com/groups/g" } } });
        (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "หนึ่ง", startAt = DateTimeOffset.UtcNow.AddMinutes(5), useDelay = false, repeat = "none",
            targets = new[] { new { accountId = pair.AccountId, groups = new[] { "G" } } },
        }, Json)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "สอง", startAt = DateTimeOffset.UtcNow.AddMinutes(6), useDelay = false, repeat = "none",
            targets = new[] { new { accountId = pair.AccountId, groups = new[] { "G" } } },
        }, Json)).EnsureSuccessStatusCode();

        using (factory.Clock.Advance(TimeSpan.FromMinutes(7)))
        {
            var job = (await (await device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            await device.PostAsJsonAsync($"/api/device/jobs/{job.PostId}/result", new { ok = true });
        }
        using (factory.Clock.Advance(TimeSpan.FromMinutes(20)))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsync("/api/device/jobs/claim", null)).StatusCode);
            var jobs = (await admin.GetFromJsonAsync<List<AdminJobDto>>($"/api/admin/jobs?customerId={auth.User.Id}", Json))!;
            Assert.Equal([PostStatus.Failed, PostStatus.Success], jobs.Select(j => j.Status));
            Assert.Equal(FailureCode.Quota, jobs[0].FailureCode);
            var c = await CustomerAsync(admin, auth.User.Id);
            Assert.Equal((1, 1), (c.Jobs.Ok, c.Jobs.Failed));

            // "Retry failed" puts it back in the queue; pausing stops the device from taking it.
            Assert.Equal(1, await (await admin.PostAsync($"/api/admin/customers/{auth.User.Id}/retry-failed", null)).Content.ReadFromJsonAsync<int>());
            await admin.PostAsJsonAsync($"/api/admin/customers/{auth.User.Id}/pause", new { paused = true }, Json);
            Assert.True((await CustomerAsync(admin, auth.User.Id)).Paused);
        }
    }
}
