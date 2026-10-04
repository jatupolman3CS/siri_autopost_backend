using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>
/// The waiting line between the requests that raise events and the worker that sends them. Bounded: when it is full
/// the oldest message is dropped (and logged), so a dead Telegram never grows the API's memory or blocks a request.
/// </summary>
public sealed class NotificationQueue
{
    private readonly Channel<NotificationJob> channel;
    private long dropped;

    public NotificationQueue(IOptions<NotificationOptions> options, ILogger<NotificationQueue> log)
    {
        channel = Channel.CreateBounded<NotificationJob>(
            new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            job =>
            {
                Interlocked.Increment(ref dropped);
                log.LogWarning("คิวแจ้งเตือนเต็ม: ทิ้งข้อความเก่าสุด ({Event})", job.Event);
            });
    }

    /// <summary>Messages dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    public ChannelReader<NotificationJob> Reader => channel.Reader;

    public bool TryWrite(NotificationJob job) => channel.Writer.TryWrite(job);
}

/// <summary>Puts the message in the queue and returns at once; <see cref="NotificationWorker"/> sends it.</summary>
public sealed class QueuedNotificationDispatcher(NotificationQueue queue) : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default)
    {
        queue.TryWrite(new NotificationJob(ev, workspaceId, linkSetId, linkId, text));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends inside the call that raised the event (<c>Notifications:Inline</c>). For the integration tests, which then know
/// what was sent as soon as their request returns. Not for production: a slow Telegram would slow the request.
/// </summary>
public sealed class InlineNotificationDispatcher(NotificationDelivery delivery) : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default) =>
        delivery.DeliverAsync(new NotificationJob(ev, workspaceId, linkSetId, linkId, text), ct);
}
