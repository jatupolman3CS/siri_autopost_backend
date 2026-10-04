using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Infrastructure.Auth;
using SIRIAUTOPOST.Infrastructure.Data;
using SIRIAUTOPOST.Infrastructure.Diagnostics;
using SIRIAUTOPOST.Infrastructure.Notifications;
using SIRIAUTOPOST.Infrastructure.Payments;
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
        services.AddScoped<IMediaFolderRepository, MediaFolderRepository>();
        services.AddSingleton<IObjectStorage, R2ObjectStorage>();
        services.AddScoped<ISnippetRepository, SnippetRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IDevicePairingRepository, DevicePairingRepository>();
        services.AddScoped<IExtensionRepository, ExtensionRepository>();
        services.AddScoped<IDeviceEventRepository, DeviceEventRepository>();
        services.AddSingleton<DeviceEventBus>();
        services.AddSingleton<IDeviceEventBus>(sp => sp.GetRequiredService<DeviceEventBus>());
        services.AddScoped<IMemberRepository, MemberRepository>();
        services.AddScoped<IPlanRepository, PlanRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IDatabaseProbe, DatabaseProbe>();
        services.AddScoped<IPromoRepository, PromoRepository>();
        services.AddScoped<IPaymentEventRepository, PaymentEventRepository>();
        services.AddScoped<IWorkspaceSeeder, DemoWorkspaceSeeder>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<ICollectionPostRepository, CollectionPostRepository>();
        services.AddScoped<ILinkSetRepository, LinkSetRepository>();
        services.AddScoped<ISetLinkRepository, SetLinkRepository>();
        services.AddScoped<IScheduleRepository, ScheduleRepository>();
        services.AddScoped<IReportShareRepository, ReportShareRepository>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IGoogleTokenVerifier>(sp => new GoogleTokenVerifier(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, sp.GetRequiredService<IConfiguration>()));
        services.Configure<StripeOptions>(config.GetSection(StripeOptions.Section));
        services.AddSingleton<IPaymentGateway, StripePaymentGateway>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IRandomSource, RandomSource>();
        services.AddSingleton<IDeviceSecrets, DeviceSecrets>();

        // Notifications (Telegram / LINE): gateway, queue and background worker
        services.AddNotifications(config);

        return services;
    }
}
