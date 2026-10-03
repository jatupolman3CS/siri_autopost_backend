using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using SIRIAUTOPOST.Api.Auth;
using SIRIAUTOPOST.Api.Middlewares;
using SIRIAUTOPOST.Application;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Infrastructure;
using SIRIAUTOPOST.Infrastructure.Auth;

namespace SIRIAUTOPOST.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public const string FrontendCors = "Frontend";
    /// <summary>The extension calls /api/device from its own origin; it sends a key header, never cookies.</summary>
    public const string DeviceCors = "Devices";

    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddApplication();
        services.AddInfrastructure(config);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<IRequestTimings, RequestTimings>();
        services.AddScoped<ICurrentDevice, CurrentDevice>();
        services.AddJwtAuth(config);

        // Enums travel as snake_case strings ("fb", "pending_approval", "agency"...) and numbers
        // only as JSON numbers. Set for MVC (responses) and for the HTTP JSON options the OpenAPI
        // document is built from, so generated client types say `number`, not `number | string`.
        var enums = new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower);
        services.AddControllers().AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.Converters.Add(enums);
            o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(enums);
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.AddProblemDetails();
        services.AddOpenApi();

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
        services.AddCors(o => o.AddPolicy(FrontendCors, p => p
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()));
        services.AddCors(o => o.AddPolicy(DeviceCors, p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        return services;
    }

    // Bearer tokens from AuthController; every endpoint requires one unless marked [AllowAnonymous].
    private static void AddJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        var key = JwtTokenService.SigningKey(jwt); // fails fast when Jwt:Key is missing or too short

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(DeviceKeyAuthenticationHandler.SchemeName, null)
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
                // A token stays valid for days: refuse it at once when the platform admin suspends the account.
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var users = ctx.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                        var id = Guid.TryParse(ctx.Principal?.FindFirst("sub")?.Value, out var g) ? g : Guid.Empty;
                        var user = await users.GetByIdAsync(id, ctx.HttpContext.RequestAborted);
                        // An admin acting as a customer may look into a suspended account; the admin must still be one.
                        var actor = ctx.Principal?.FindFirst(JwtTokenService.ActorClaim)?.Value;
                        if (actor is not null)
                        {
                            var admin = Guid.TryParse(actor, out var a) ? await users.GetByIdAsync(a, ctx.HttpContext.RequestAborted) : null;
                            if (user is null || admin is not { Role: UserRole.Admin }) ctx.Fail("impersonation no longer allowed");
                        }
                        else if (user is null || user.IsBlocked) ctx.Fail("account blocked or deleted");
                    },
                };
            });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }
}
