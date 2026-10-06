using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>
/// Every <see cref="NotificationOptions.DeviceWatchEvery"/> it asks <see cref="CheckStalledDevicesCommand"/> whether a machine
/// has gone silent while posts are due for it (and whether it is back), which is what the "extension offline" message is
/// about. Off with <c>Notifications:DeviceWatch=false</c> (the integration tests call the command themselves).
/// </summary>
public sealed class DeviceWatchWorker(IServiceScopeFactory scopes, IOptions<NotificationOptions> options, ILogger<DeviceWatchWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = options.Value.DeviceWatchEvery;
        try
        {
            // Not at the very start: the database may still be migrating.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(every);
            do
            {
                await SweepAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<CheckStalledDevicesCommand, int>>();
            var told = await handler.HandleAsync(new CheckStalledDevicesCommand(), ct);
            if (told > 0) log.LogInformation("{Count} device messages sent", told);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never take the host down; the next round tries again.
            log.LogError(ex, "Watching the devices failed");
        }
    }
}
