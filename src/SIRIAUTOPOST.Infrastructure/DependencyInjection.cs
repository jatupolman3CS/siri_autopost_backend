using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Infrastructure.Auth;
using SIRIAUTOPOST.Infrastructure.Data;
using SIRIAUTOPOST.Infrastructure.Repositories;
using SIRIAUTOPOST.Infrastructure.Seeding;
using SIRIAUTOPOST.Infrastructure.Services;

namespace SIRIAUTOPOST.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ยังไม่ได้ตั้ง ConnectionStrings:Default");

        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IPostRepository, PostRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<ISnippetRepository, SnippetRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IDevicePairingRepository, DevicePairingRepository>();
        services.AddScoped<IWorkspaceSeeder, DemoWorkspaceSeeder>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IRandomSource, RandomSource>();
        services.AddSingleton<IDeviceSecrets, DeviceSecrets>();

        return services;
    }
}
