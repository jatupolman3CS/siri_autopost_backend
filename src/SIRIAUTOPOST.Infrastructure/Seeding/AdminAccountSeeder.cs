using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

// Creates the platform-owner account from Admin:Email / Admin:Password on first start.
public static class AdminAccountSeeder
{
    public static async Task EnsureAdminAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var config = sp.GetRequiredService<IConfiguration>();
        var email = User.NormalizeEmail(config["Admin:Email"]);
        var password = config["Admin:Password"];
        if (email.Length == 0 || string.IsNullOrEmpty(password)) return;

        var db = sp.GetRequiredService<AppDbContext>();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;

        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        var admin = User.Create(email, "Admin", UserRole.Admin, PlanKey.Agency, now);
        admin.SetPasswordHash(sp.GetRequiredService<IPasswordHasher>().Hash(admin, password));
        db.Users.Add(admin);
        var ws = Workspace.Create(admin.Id, "AutoPost HQ", now);
        db.Workspaces.Add(ws);
        await sp.GetRequiredService<IWorkspaceSeeder>().SeedAsync(ws, ct);
        await db.SaveChangesAsync(ct);
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seeding").LogInformation("สร้างบัญชีผู้ดูแลแพลตฟอร์ม {Email}", email);
    }
}
