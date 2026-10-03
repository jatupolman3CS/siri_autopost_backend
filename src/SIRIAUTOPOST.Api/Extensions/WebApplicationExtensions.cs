using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Api.Middlewares;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Api.Extensions;

public static class WebApplicationExtensions
{
    public static WebApplication UseApi(this WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        if (app.Environment.IsDevelopment()) app.MapOpenApi(); // /openapi/v1.json

        app.UseCors(ServiceCollectionExtensions.FrontendCors);
        app.MapControllers();
        app.MapGet("/healthz", () => Results.Ok(new { ok = true })).ExcludeFromDescription();
        return app;
    }

    // Applies pending EF Core migrations when Database:MigrateOnStartup is true (on in Development).
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) return;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }
}
