using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Api.Middlewares;
using SIRIAUTOPOST.Infrastructure.Data;
using SIRIAUTOPOST.Infrastructure.Seeding;

namespace SIRIAUTOPOST.Api.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseApi(this WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors(ServiceCollectionExtensions.FrontendCors);
        app.UseAuthentication();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous(); // /openapi/v1.json
        app.MapControllers();
        app.MapGet("/healthz", () => Results.Ok(new { ok = true })).AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    // Applies pending EF Core migrations when Database:MigrateOnStartup is true (on in Development),
    // then creates the platform-admin account from Admin:Email / Admin:Password if it does not exist.
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }
        await AdminAccountSeeder.EnsureAdminAsync(app.Services);
    }
}
