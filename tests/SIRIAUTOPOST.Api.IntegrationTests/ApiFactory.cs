using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SIRIAUTOPOST.Api.IntegrationTests.Notifications;
using SIRIAUTOPOST.Api.IntegrationTests.Payments;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.ValueObjects;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Runs the real API against a throwaway PostgreSQL database, recreated once per test run.
// Override the server with SIRIAUTOPOST_TEST_DB (an Npgsql connection string).
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "admin-password";
    public const string StripeWebhookSecret = "whsec_integration_test";
    public const string PublishableKey = "pk_test_integration";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The API's clock. Tests that move it must put it back (see <see cref="TestClock.Advance"/>).</summary>
    public TestClock Clock { get; } = new();

    /// <summary>Stripe, played by the tests. Tests that switch it off or make it decline must put it back.</summary>
    public FakePaymentGateway Payments { get; } = new(StripeWebhookSecret);

    /// <summary>The AI writer, played by the tests (a real key is never used). Tests that switch it off must put it back.</summary>
    public FakeAiWriter Ai { get; } = new();

    /// <summary>Telegram and LINE, played by the tests: notifications are delivered inline and recorded here.</summary>
    public FakeNotificationGateway Notifications { get; } = new();

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("SIRIAUTOPOST_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=siriautopost_test;Username=postgres;Password=postgres";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Jwt:Key", "integration-test-signing-key-0123456789abcdef");
        builder.UseSetting("Admin:Email", AdminEmail);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Notifications:Inline", "true"); // delivered inside the request, so tests see what was sent at once
        builder.UseSetting("Stripe:SecretKey", "sk_test_integration"); // the gateway is the fake; the keys make /billing/payment-config answer
        builder.UseSetting("Stripe:PublishableKey", PublishableKey);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.RemoveAll<IPaymentGateway>();
            s.AddSingleton<IPaymentGateway>(Payments);
            s.RemoveAll<IAiWriter>();
            s.AddSingleton<IAiWriter>(Ai);
            s.RemoveAll<INotificationGateway>();
            s.AddSingleton<INotificationGateway>(Notifications);
        });
    }

    public async Task InitializeAsync()
    {
        // The admin is seeded at startup, so the schema must exist before the host starts.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }
        _ = Services; // start the host
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>A client signed in as the platform admin seeded at startup.</summary>
    public async Task<HttpClient> AdminAsync()
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = AdminEmail, password = AdminPassword }, Json);
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return client;
    }

    /// <summary>
    /// Signs up a fresh user (always on Free) and returns a client carrying their token, plus their first workspace.
    /// With a paid <paramref name="plan"/> the platform admin grants it, as it would a complimentary plan.
    /// </summary>
    public async Task<(HttpClient Client, AuthResultDto Auth, Guid WorkspaceId)> SignUpAsync(string? plan = null, string? email = null)
    {
        var client = CreateClient();
        email ??= $"u{Guid.NewGuid():N}@shop.co";
        var res = await client.PostAsJsonAsync("/api/auth/signup", new { email, password = "password1" }, Json);
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        if (plan is not null and not "free")
        {
            var admin = await AdminAsync();
            (await admin.PutAsJsonAsync($"/api/admin/customers/{auth.User.Id}/plan", new { plan }, Json)).EnsureSuccessStatusCode();
            auth = auth with { User = (await client.GetFromJsonAsync<UserDto>("/api/auth/me", Json))! };
        }
        var ws = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!;
        return (client, auth, ws[0].Id);
    }

    /// <summary>Delivers a webhook to the API the way Stripe does: raw JSON body, signed with the endpoint secret.</summary>
    public async Task<HttpResponseMessage> WebhookAsync(string json, string? secret = StripeWebhookSecret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        if (secret is not null) req.Headers.Add("Stripe-Signature", StripeEvents.Signature(json, secret));
        return await CreateClient().SendAsync(req);
    }

    public async Task SendAsync(string type, object data, string? id = null) =>
        (await WebhookAsync(StripeEvents.Event(type, data, id))).EnsureSuccessStatusCode();

    /// <summary>
    /// The customer subscribes the way the web app does: chooses a plan, pays on Stripe's page, and Stripe tells us
    /// (checkout.session.completed, then invoice.paid for the first payment). Returns the subscription id.
    /// </summary>
    public async Task<string> SubscribeAsync(
        HttpClient client, AuthResultDto auth, PlanKey plan, BillingCycle cycle = BillingCycle.Month, string? promoCode = null, decimal? firstInvoice = null)
    {
        var res = await client.PutAsJsonAsync("/api/billing/plan", new { plan, cycle, promoCode }, Json);
        res.EnsureSuccessStatusCode();
        var change = (await res.Content.ReadFromJsonAsync<PlanChangeDto>(Json))!;
        Assert.NotNull(change.CheckoutUrl);
        var session = Payments.LastSessionOf(auth.User.Id);
        var request = Payments.Session(session);
        var subscription = Payments.PayCheckout(session);
        var customer = Payments.CustomerOf(auth.User.Id);
        var (p, c) = (AuditEntryKey(plan), cycle == BillingCycle.Year ? "year" : "month");
        await SendAsync("checkout.session.completed", StripeEvents.Session(session, customer, subscription, auth.User.Id, p, c, request.PromoCode));
        var amount = firstInvoice ?? Pricing.Period(request.Price, cycle) - request.FirstDiscount;
        await SendAsync("invoice.paid", StripeEvents.Invoice(
            $"in_{Guid.NewGuid():N}"[..20], customer, subscription, amount, auth.User.Id, p, c, "subscription_create", request.PromoCode));
        return subscription;
    }

    private static string AuditEntryKey(PlanKey plan) => plan.ToString().ToLowerInvariant();
}

/// <summary>System time plus an offset a test can move forward (and must reset).</summary>
public sealed class TestClock : TimeProvider
{
    private TimeSpan offset;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;

    /// <summary>Moves the clock; dispose the result to move it back.</summary>
    public IDisposable Advance(TimeSpan by)
    {
        offset += by;
        return new Reset(() => offset -= by);
    }

    private sealed class Reset(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>
/// All API test classes share one factory (and so one database), and run one after another:
/// separate fixtures would drop each other's database. Each test signs up its own user.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>An AI writer that answers at once with numbered drafts of the topic (or is switched off, like a server with no key).</summary>
public sealed class FakeAiWriter : IAiWriter
{
    public bool IsOn { get; set; } = true;
    public bool Enabled => IsOn;
    public string Model => "fake-model";
    public List<AiDraftRequest> Requests { get; } = [];

    public Task<IReadOnlyList<string>> DraftAsync(AiDraftRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(1, request.Count).Select(i => $"{{{{code}}}}\nร่าง {i}: {request.Topic}").ToList());
    }
}
