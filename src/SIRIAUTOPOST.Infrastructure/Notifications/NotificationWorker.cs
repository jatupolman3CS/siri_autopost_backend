using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>
/// Sends the queued notifications one after another (so they arrive in the order they happened). On shutdown it stops
/// waiting for new ones and spends a few seconds sending what is already queued, then ends.
/// </summary>
public sealed class NotificationWorker(NotificationQueue queue, NotificationDelivery delivery, ILogger<NotificationWorker> log) : BackgroundService
{
    /// <summary>How long a stopping host may spend sending what is still queued.</summary>
    public static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
                while (queue.Reader.TryRead(out var job))
                    await delivery.DeliverAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }

        using var drain = new CancellationTokenSource(DrainTime);
        try
        {
            while (queue.Reader.TryRead(out var job)) await delivery.DeliverAsync(job, drain.Token);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("หยุดส่งการแจ้งเตือนที่ค้างในคิวเพราะระบบกำลังปิด");
        }
    }
}
