using System.Security.Claims;
using SIRI.AUTOPOST.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace SIRI.AUTOPOST.Server.Endpoints;

public static class AuthEndpoints
{
    public record LoginRequest(string? Username, string? Password);
    public record PasswordRequest(string? Current, string? New);

    static readonly PasswordHasher<User> Hasher = new();

    public static string HashPassword(User u, string password) => Hasher.HashPassword(u, password);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/login", async (LoginRequest req, AppDb db, HttpContext ctx) =>
        {
            var name = (req.Username ?? "").Trim();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == name);
            var ok = user is not null &&
                     Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") != PasswordVerificationResult.Failed;
            if (!ok)
            {
                await Task.Delay(Random.Shared.Next(400, 900)); // slow down guessing
                return Results.Json(new { error = "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง" }, statusCode: 401);
            }
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user!.Id.ToString()), new Claim(ClaimTypes.Name, user.Username)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Ok(new { username = user.Username });
        });

        g.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        g.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { username = user.Identity!.Name }))
            .RequireAuthorization();

        g.MapPost("/password", async (PasswordRequest req, AppDb db, ClaimsPrincipal principal) =>
        {
            var id = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.FindAsync(id);
            if (user is null) return Results.Unauthorized();
            if (Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Current ?? "") == PasswordVerificationResult.Failed)
                return Results.Json(new { error = "รหัสผ่านเดิมไม่ถูกต้อง" }, statusCode: 400);
            if ((req.New ?? "").Length < 8)
                return Results.Json(new { error = "รหัสผ่านใหม่ต้องยาวอย่างน้อย 8 ตัว" }, statusCode: 400);
            user.PasswordHash = HashPassword(user, req.New!);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();
    }
}
