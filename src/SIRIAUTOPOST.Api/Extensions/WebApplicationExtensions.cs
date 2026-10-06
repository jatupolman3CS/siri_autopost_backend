using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Api.Middlewares;
using SIRIAUTOPOST.Infrastructure.Data;
using SIRIAUTOPOST.Infrastructure.Seeding;
using SIRIAUTOPOST.Infrastructure.Services;

namespace SIRIAUTOPOST.Api.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseApi(this WebApplication app)
    {
        app.UseMiddleware<RequestTimingMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors(ServiceCollectionExtensions.FrontendCors);
        app.UseAuthentication();
        app.UseMiddleware<ReadOnlyImpersonationMiddleware>();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous(); // /openapi/v1.json
        app.MapControllers();
        app.MapGet("/healthz", () => Results.Ok(new { ok = true })).AllowAnonymous().ExcludeFromDescription();

        // Shutting down: end every event stream and waiting device sync at once (they reconnect to the next
        // instance) instead of holding the host open until Kestrel's shutdown timeout.
        var events = app.Services.GetRequiredService<DeviceEventBus>();
        app.Lifetime.ApplicationStopping.Register(events.Stop);
        return app;
    }

    // Applies pending EF Core migrations when Database:MigrateOnStartup is true (on in Development),
    // then creates the platform-admin account from Admin:Email / Admin:Password if it does not exist, or makes the
    // existing account with that address an admin.
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
