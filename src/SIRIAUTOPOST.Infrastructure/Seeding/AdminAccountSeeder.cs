using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

// Creates the platform-owner account from Admin:Email / Admin:Password on first start. An account that already exists
// under Admin:Email (a customer who signed up first) is made an admin on the top plan instead, keeping its password.
// The owner account is on Agency like a freshly seeded one (an admin is not in the customer list, so the plan cannot
// be granted there); a plan paid through Stripe is left alone.
public static class AdminAccountSeeder
{
    public static async Task EnsureAdminAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var config = sp.GetRequiredService<IConfiguration>();
        var email = User.NormalizeEmail(config["Admin:Email"]);
        var password = config["Admin:Password"];
        if (email.Length == 0) return;

        var db = sp.GetRequiredService<AppDbContext>();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (existing is not null)
        {
            var promoted = existing.Role != UserRole.Admin;
            var upgraded = existing.Plan != PlanKey.Agency && !existing.HasSubscription;
            if (!promoted && !upgraded) return;
            if (promoted) existing.PromoteToAdmin();
            if (upgraded) existing.SetPlanByAdmin(PlanKey.Agency);
            await db.SaveChangesAsync(ct);
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seeding").LogInformation(
                "บัญชีผู้ดูแลแพลตฟอร์ม {Email}: เลื่อนเป็นผู้ดูแล={Promoted} ตั้งแผนสูงสุด={Upgraded}", email, promoted, upgraded);
            return;
        }
        if (string.IsNullOrEmpty(password)) return;

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
