using System.Net;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class BillingProfileAndPublicTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    private static Task<BillingProfileDto> ProfileAsync(HttpClient c) =>
        c.GetFromJsonAsync<BillingProfileDto>("/api/billing/profile", Json)!;

    [Fact]
    public async Task A_new_customer_has_no_card_and_the_default_notifications()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var p = await ProfileAsync(client);
        Assert.Equal((true, true, false, null, false), (p.NotifyFailed, p.NotifyExpiring, p.NotifyRenewal, p.Card, p.PaymentsConnected));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/billing/profile")).StatusCode);
    }

    [Fact]
    public async Task Notifications_and_the_card_are_saved_and_the_expiry_state_is_worked_out()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var n = await client.PutAsJsonAsync("/api/billing/profile/notifications", new { notifyFailed = false, notifyExpiring = true, notifyRenewal = true }, Json);
        n.EnsureSuccessStatusCode();
        Assert.Equal((false, true, true), ((await ProfileAsync(client)) is var p ? (p.NotifyFailed, p.NotifyExpiring, p.NotifyRenewal) : default));

        var now = DateTimeOffset.UtcNow;
        var far = await client.PutAsJsonAsync("/api/billing/profile/payment-method", new { brand = "Visa", last4 = "4242", expMonth = 12, expYear = now.Year + 3 }, Json);
        var card = (await far.Content.ReadFromJsonAsync<BillingProfileDto>(Json))!.Card!;
        Assert.Equal(("visa", "4242", false, false), (card.Brand, card.Last4, card.Expired, card.ExpiresSoon));

        // Expiring this month: still valid, but soon. The notification choices stay.
        var soon = await client.PutAsJsonAsync("/api/billing/profile/payment-method", new { brand = "mastercard", last4 = "5100", expMonth = now.Month, expYear = now.Year }, Json);
        var after = (await soon.Content.ReadFromJsonAsync<BillingProfileDto>(Json))!;
        Assert.Equal((false, true), (after.Card!.Expired, after.Card.ExpiresSoon));
        Assert.False(after.NotifyFailed);

        var removed = await client.DeleteAsync("/api/billing/profile/payment-method");
        Assert.Null((await removed.Content.ReadFromJsonAsync<BillingProfileDto>(Json))!.Card);
        Assert.Null((await ProfileAsync(client)).Card);
    }

    [Theory]
    [InlineData("42", 12, 2030, HttpStatusCode.BadRequest)]
    [InlineData("4242", 13, 2030, HttpStatusCode.BadRequest)]
    [InlineData("4242", 1, 2020, HttpStatusCode.UnprocessableEntity)] // expired
    public async Task Bad_cards_are_refused(string last4, int month, int year, HttpStatusCode expected)
    {
        var (client, _, _) = await factory.SignUpAsync();
        var res = await client.PutAsJsonAsync("/api/billing/profile/payment-method", new { brand = "visa", last4, expMonth = month, expYear = year }, Json);
        Assert.Equal(expected, res.StatusCode);
        Assert.Null((await ProfileAsync(client)).Card);
    }

    [Fact]
    public async Task A_statement_is_a_printable_page_for_the_customers_own_charge_only()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        (await client.PutAsJsonAsync("/api/auth/me/plan", new { plan = "pro", cycle = "month" }, Json)).EnsureSuccessStatusCode();
        var tx = (await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!.Single();

        var res = await client.GetAsync($"/api/billing/invoices/{tx.Id}/statement");
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("ใบแสดงรายการเรียกเก็บเงิน", html);
        Assert.Contains("฿790", html);
        Assert.Contains(auth.User.Email, html);
        Assert.Contains("ยังไม่ได้เชื่อมระบบตัดบัตร", html); // not a receipt: nothing was collected

        var (other, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/billing/invoices/{tx.Id}/statement")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/billing/invoices/{tx.Id}/statement")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/billing/invoices/{Guid.NewGuid()}/statement")).StatusCode);
    }

    [Fact]
    public async Task The_statement_page_escapes_the_customers_name()
    {
        var (client, auth, _) = await factory.SignUpAsync();
        (await client.PutAsJsonAsync("/api/auth/me/plan", new { plan = "basic", cycle = "month" }, Json)).EnsureSuccessStatusCode();
        var tx = (await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!.Single();
        // Names come from the sign-up form or Google; whatever they hold must not become markup.
        Assert.Contains(System.Net.WebUtility.HtmlEncode(auth.User.Name), await (await client.GetAsync($"/api/billing/invoices/{tx.Id}/statement")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Public_stats_are_anonymous_and_ignore_sample_data()
    {
        var anon = factory.CreateClient();
        var before = (await anon.GetFromJsonAsync<PublicStatsDto>("/api/public/stats", Json))!;
        Assert.Equal(7, before.Days.Count);
        Assert.Equal(before.Days.Select(d => d.Date).OrderBy(d => d), before.Days.Select(d => d.Date));
        Assert.Equal((before.PostsSent7d, before.PostsFailed7d), (before.Days.Sum(d => d.Sent), before.Days.Sum(d => d.Failed)));

        // A new workspace brings a week of sample history: none of it is a post that went out.
        await factory.SignUpAsync();
        var after = (await anon.GetFromJsonAsync<PublicStatsDto>("/api/public/stats", Json))!;
        Assert.Equal((before.PostsSent7d, before.PostsFailed7d), (after.PostsSent7d, after.PostsFailed7d));
    }

    [Fact]
    public async Task Public_stats_count_posts_that_really_went_out_through_a_device()
    {
        var anon = factory.CreateClient();
        // Measured at the same (advanced) time as "after", so posts other tests left due do not count as ours.
        PublicStatsDto before;
        using (factory.Clock.Advance(TimeSpan.FromMinutes(6)))
            before = (await anon.GetFromJsonAsync<PublicStatsDto>("/api/public/stats", Json))!;

        var (owner, _, ws) = await factory.SignUpAsync();
        var code = (await (await owner.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).Content.ReadFromJsonAsync<PairingCodeDto>(Json))!;
        var device = factory.CreateClient();
        var pair = (await (await device.PostAsJsonAsync("/api/device/pair", new { code = code.Code, name = "PC", browser = "Chrome", version = "2.2.0" }, Json))
            .Content.ReadFromJsonAsync<PairResultDto>(Json))!;
        device.DefaultRequestHeaders.Add("X-Device-Key", pair.DeviceKey);
        await device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "Plants", url = "https://www.facebook.com/groups/plants" } } });
        var at = DateTimeOffset.UtcNow.AddMinutes(5);
        (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", new
        {
            content = "x", startAt = at, useDelay = false, repeat = "none",
            targets = new[] { new { accountId = pair.AccountId, groups = new[] { "Plants" } } },
        }, Json)).EnsureSuccessStatusCode();

        PublicStatsDto after;
        using (factory.Clock.Advance(TimeSpan.FromMinutes(6))) // the post is due, so "now" is after its time
        {
            var job = (await (await device.PostAsync("/api/device/jobs/claim", null)).Content.ReadFromJsonAsync<JobDto>(Json))!;
            (await device.PostAsJsonAsync($"/api/device/jobs/{job.PostId}/result", new { ok = true })).EnsureSuccessStatusCode();
            after = (await anon.GetFromJsonAsync<PublicStatsDto>("/api/public/stats", Json))!;
        }

        Assert.Equal(before.PostsSent7d + 1, after.PostsSent7d);
        Assert.True(after.DevicesActive24h >= 1);
        Assert.NotNull(after.SuccessRate7d);
    }

    [Fact]
    public async Task New_workspaces_are_empty_unless_demo_seeding_is_on()
    {
        using var empty = factory.WithWebHostBuilder(b => b.UseSetting("Demo:SeedNewWorkspaces", "false"));
        var client = empty.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/signup", new { email = $"e{Guid.NewGuid():N}@shop.co", password = "password1" }, Json);
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Token);
        var ws = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))![0].Id;

        Assert.Empty((await client.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!);
        Assert.Empty((await client.GetFromJsonAsync<List<SnippetDto>>($"/api/workspaces/{ws}/snippets", Json))!);
        Assert.Empty((await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/errors", Json))!);
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(30).ToString("O"));
        Assert.Empty((await client.GetFromJsonAsync<List<PostDto>>($"/api/workspaces/{ws}/posts?from={from}&to={to}", Json))!);
        var engine = (await client.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!;
        Assert.Equal(0, engine.Devices);
    }
}
