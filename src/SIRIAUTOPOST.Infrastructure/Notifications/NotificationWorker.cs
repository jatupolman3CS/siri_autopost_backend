using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>
/// Sends the queued notifications one after another (so they arrive in the order they happened). When the host stops it
/// stops waiting for new ones, but keeps sending what is already queued (including the message in flight) for at most
/// <see cref="NotificationOptions.ShutdownGrace"/>; whatever is left after that is dropped, so a dead Telegram cannot hold up a deploy.
/// </summary>
public sealed class NotificationWorker(
    NotificationQueue queue, NotificationDelivery delivery, IOptions<NotificationOptions> options, ILogger<NotificationWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Cancelled a grace period after the host asks us to stop: the budget for flushing the queue.
        using var grace = new CancellationTokenSource();
        using var registration = stoppingToken.Register(() => grace.CancelAfter(options.Value.ShutdownGrace));

        while (true)
        {
            if (grace.IsCancellationRequested)
            {
                log.LogWarning("ระบบกำลังปิด: ทิ้งการแจ้งเตือนที่ยังค้างในคิวเพราะหมดเวลาส่ง");
                return;
            }
            if (queue.TryRead(out var job))
            {
                await delivery.DeliverAsync(job, grace.Token);
                continue;
            }
            if (stoppingToken.IsCancellationRequested) return; // stopping and nothing is left
            try
            {
                if (!await queue.Reader.WaitToReadAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                // Stopping: go round once more to flush what arrived meanwhile.
            }
        }
    }
}
