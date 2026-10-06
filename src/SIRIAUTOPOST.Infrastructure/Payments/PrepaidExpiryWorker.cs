using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Infrastructure.Payments;

/// <summary>
/// Sends customers whose prepaid plan (a PromptPay payment for one period) has run out back to Free. A subscription
/// ends through Stripe's events, but nothing at Stripe ends a prepaid plan, so this looks every few minutes.
/// </summary>
public sealed class PrepaidExpiryWorker(IServiceScopeFactory scopes, ILogger<PrepaidExpiryWorker> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not at the very start: the database may still be migrating.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(Every);
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
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EndPrepaidPlansCommand, int>>();
            var ended = await handler.HandleAsync(new EndPrepaidPlansCommand(), ct);
            if (ended > 0) log.LogInformation("{Count} prepaid plans ended: back to Free", ended);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never take the host down; the next round tries again.
            log.LogError(ex, "Ending prepaid plans failed");
        }
    }
}
