using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

public static class NotificationRegistration
{
    /// <summary>
    /// The real notification system: the HTTP gateway, and a dispatcher that queues for a background worker (or, with
    /// <c>Notifications:Inline</c>, sends at once). It replaces the application's no-op dispatcher.
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NotificationOptions>(config.GetSection(NotificationOptions.Section));
        services.AddSingleton<INotificationGateway>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NotificationOptions>>();
            // The gateway enforces the timeout itself; the client's own is only a backstop. No IHttpClientFactory: its
            // request logging would write Telegram addresses (which hold the bot token) to the log.
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                Timeout = options.Value.Timeout + TimeSpan.FromSeconds(5),
            };
            return new HttpNotificationGateway(http, options, sp.GetRequiredService<ILogger<HttpNotificationGateway>>());
        });
        services.AddSingleton<NotificationDelivery>();
        if (config.GetValue($"{NotificationOptions.Section}:{nameof(NotificationOptions.DeviceWatch)}", true))
            services.AddHostedService<DeviceWatchWorker>();

        services.RemoveAll<INotificationDispatcher>(); // the application registers a no-op one by default
        if (config.GetValue<bool>($"{NotificationOptions.Section}:{nameof(NotificationOptions.Inline)}"))
        {
            services.AddSingleton<INotificationDispatcher, InlineNotificationDispatcher>();
        }
        else
        {
            services.AddSingleton<NotificationQueue>();
            services.AddSingleton<INotificationDispatcher, QueuedNotificationDispatcher>();
            services.AddHostedService<NotificationWorker>();
        }
        return services;
    }
}
