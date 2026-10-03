using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Runs the real API against a throwaway PostgreSQL database, recreated once per test run.
// Override the server with SIRIAUTOPOST_TEST_DB (an Npgsql connection string).
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "admin-password";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The API's clock. Tests that move it must put it back (see <see cref="TestClock.Advance"/>).</summary>
    public TestClock Clock { get; } = new();

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
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
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

    /// <summary>Signs up a fresh user and returns a client carrying their token, plus their first workspace.</summary>
    public async Task<(HttpClient Client, AuthResultDto Auth, Guid WorkspaceId)> SignUpAsync(string? plan = null, string? email = null)
    {
        var client = CreateClient();
        email ??= $"u{Guid.NewGuid():N}@shop.co";
        var res = await client.PostAsJsonAsync("/api/auth/signup", new { email, password = "password1", plan }, Json);
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        var ws = (await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!;
        return (client, auth, ws[0].Id);
    }
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
