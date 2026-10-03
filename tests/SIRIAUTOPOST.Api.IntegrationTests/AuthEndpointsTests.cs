using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    [Fact]
    public async Task Sign_up_returns_a_token_that_opens_the_api()
    {
        var (client, auth, _) = await factory.SignUpAsync("pro");

        Assert.Equal(PlanKey.Pro, auth.User.Plan);
        var me = await client.GetFromJsonAsync<UserDto>("/api/auth/me", Json);
        Assert.Equal(auth.User.Email, me!.Email);
        Assert.Equal(UserRole.User, me.Role);
    }

    [Fact]
    public async Task Everything_but_sign_up_and_log_in_needs_a_token()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_is_a_conflict_and_a_wrong_password_is_unauthorized()
    {
        var (_, auth, _) = await factory.SignUpAsync();
        var anon = factory.CreateClient();

        var dup = await anon.PostAsJsonAsync("/api/auth/signup", new { email = auth.User.Email.ToUpperInvariant(), password = "password1" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var bad = await anon.PostAsJsonAsync("/api/auth/login", new { email = auth.User.Email, password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var problem = await bad.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("อีเมลหรือรหัสผ่านไม่ถูกต้อง", problem!.Title);

        var ok = await anon.PostAsJsonAsync("/api/auth/login", new { email = auth.User.Email, password = "password1" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Sign_up_validates_input()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/signup", new { email = "nope", password = "123" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("email", problem!.Errors.Keys);
        Assert.Contains("password", problem.Errors.Keys);
    }

    [Fact]
    public async Task The_admin_is_seeded_from_configuration()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });
        var auth = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal(UserRole.Admin, auth.User.Role);
        Assert.Equal(PlanKey.Agency, auth.User.Plan);
    }

    [Fact]
    public async Task Plan_can_be_changed()
    {
        var (client, _, _) = await factory.SignUpAsync();
        var res = await client.PutAsJsonAsync("/api/auth/me/plan", new { plan = "agency" });
        var me = await res.Content.ReadFromJsonAsync<UserDto>(Json);
        Assert.Equal(PlanKey.Agency, me!.Plan);
    }

    private sealed class FakeGoogle : IGoogleTokenVerifier
    {
        public string? ClientId => "test-client.apps.googleusercontent.com";
        public Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken ct = default) =>
            idToken.StartsWith("ok:") ? Task.FromResult(new GoogleIdentity(idToken[3..], "Google Person"))
                : throw new AuthenticationException("bad token");
    }

    [Fact]
    public async Task Google_sign_in_creates_then_reuses_the_account_and_rejects_bad_tokens()
    {
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IGoogleTokenVerifier, FakeGoogle>())).CreateClient();
        var email = $"g{Guid.NewGuid():N}@gmail.com";

        var cfg = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/config", Json);
        Assert.Equal("test-client.apps.googleusercontent.com", cfg.GetProperty("googleClientId").GetString());

        var first = await client.PostAsJsonAsync("/api/auth/google", new { idToken = "ok:" + email, plan = "pro" }, Json);
        first.EnsureSuccessStatusCode();
        var a = (await first.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal(email, a.User.Email);
        Assert.Equal(PlanKey.Pro, a.User.Plan);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
        Assert.NotEmpty((await client.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!);

        var again = (await (await client.PostAsJsonAsync("/api/auth/google", new { idToken = "ok:" + email }, Json))
            .Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal(a.User.Id, again.User.Id);

        // No password was set, so password login must fail.
        var pw = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "" }, Json);
        Assert.NotEqual(HttpStatusCode.OK, pw.StatusCode);

        var bad = await client.PostAsJsonAsync("/api/auth/google", new { idToken = "nope" }, Json);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }
}
