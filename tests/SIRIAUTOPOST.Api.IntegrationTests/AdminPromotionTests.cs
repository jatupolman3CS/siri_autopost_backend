using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AdminPromotionTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    [Fact]
    public async Task A_customer_who_already_exists_under_Admin_Email_becomes_an_admin_at_startup()
    {
        var (customer, auth, _) = await factory.SignUpAsync(email: $"boss{Guid.NewGuid():N}@shop.co");
        Assert.Equal(UserRole.User, auth.User.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/admin/customers")).StatusCode);

        // The next start of the API names that address in Admin:Email (the host runs the seeder when it starts).
        using var restarted = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Email", auth.User.Email));
        var client = restarted.CreateClient();

        // The password stays; the role and the top plan change, and a new token carries the role.
        Assert.Equal(PlanKey.Free, auth.User.Plan);
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = auth.User.Email, password = "password1" }, Json);
        res.EnsureSuccessStatusCode();
        var login = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal(UserRole.Admin, login.User.Role);
        Assert.Equal(PlanKey.Agency, login.User.Plan);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/customers")).StatusCode);
    }

    [Fact]
    public async Task A_token_issued_before_the_promotion_is_an_admin_token_at_once()
    {
        // The customer signed in before they became the admin: their token still says "user", /auth/me says "admin".
        var (customer, auth, _) = await factory.SignUpAsync(email: $"early{Guid.NewGuid():N}@shop.co");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/admin/customers")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Users.SingleAsync(u => u.Email == auth.User.Email);
            row.PromoteToAdmin();
            await db.SaveChangesAsync();
        }

        // No new sign-in: the same token now opens the admin pages (the role is read from the database).
        Assert.Equal(UserRole.Admin, (await customer.GetFromJsonAsync<UserDto>("/api/auth/me", Json))!.Role);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/admin/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/admin/health")).StatusCode);
    }

    [Fact]
    public async Task Another_customer_is_not_touched_by_an_Admin_Email_that_names_someone_else()
    {
        var (_, other, _) = await factory.SignUpAsync();
        using var restarted = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Email", ApiFactory.AdminEmail));
        var client = restarted.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email = other.User.Email, password = "password1" }, Json);
        res.EnsureSuccessStatusCode();
        var user = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!.User;
        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal(other.User.Plan, user.Plan);
    }

    [Fact]
    public async Task An_admin_made_by_the_database_alone_is_moved_to_the_top_plan_at_the_next_start()
    {
        // Admin by role only (as a hand-run UPDATE leaves it), still on Free.
        var (_, auth, _) = await factory.SignUpAsync(email: $"owner{Guid.NewGuid():N}@shop.co");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Users.SingleAsync(u => u.Email == auth.User.Email);
            row.PromoteToAdmin();
            await db.SaveChangesAsync();
            Assert.Equal(PlanKey.Free, row.Plan);
        }
        using var again = factory.WithWebHostBuilder(b => b.UseSetting("Admin:Email", auth.User.Email));
        var res = await again.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = auth.User.Email, password = "password1" }, Json);
        res.EnsureSuccessStatusCode();
        var user = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!.User;
        Assert.Equal((UserRole.Admin, PlanKey.Agency), (user.Role, user.Plan));
    }
}
