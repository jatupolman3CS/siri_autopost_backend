using SIRIAUTOPOST.Application;
using SIRIAUTOPOST.Infrastructure;

namespace SIRIAUTOPOST.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public const string FrontendCors = "Frontend";

    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddApplication();
        services.AddInfrastructure(config);

        services.AddControllers();
        services.AddProblemDetails();
        services.AddOpenApi();

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
        services.AddCors(o => o.AddPolicy(FrontendCors, p => p
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        return services;
    }
}
