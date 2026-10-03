using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using SIRIAUTOPOST.Api.Auth;
using SIRIAUTOPOST.Application;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Infrastructure;
using SIRIAUTOPOST.Infrastructure.Auth;

namespace SIRIAUTOPOST.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public const string FrontendCors = "Frontend";

    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddApplication();
        services.AddInfrastructure(config);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddJwtAuth(config);

        // Enums travel as snake_case strings: "fb", "pending_approval", "agency"...
        // Set for MVC (responses) and for the HTTP JSON options the OpenAPI document is built from.
        var enums = new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower);
        services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(enums));
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(enums));
        services.AddProblemDetails();
        services.AddOpenApi();

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
        services.AddCors(o => o.AddPolicy(FrontendCors, p => p
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()));

        return services;
    }

    // Bearer tokens from AuthController; every endpoint requires one unless marked [AllowAnonymous].
    private static void AddJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        var key = JwtTokenService.SigningKey(jwt); // fails fast when Jwt:Key is missing or too short

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = key,
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }
}
