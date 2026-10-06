using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>
/// The waiting line between the requests that raise events and the worker that sends them. Bounded: when it is full
/// the oldest message is dropped (and logged), so a dead Telegram never grows the API's memory or blocks a request. The
/// pictures that wait are bounded too (<see cref="MaxQueuedPhotoBytes"/>): past that a message waits without its picture.
/// </summary>
public sealed class NotificationQueue
{
    /// <summary>Pictures waiting in the queue may take this much memory in all.</summary>
    public const long MaxQueuedPhotoBytes = 32L * 1024 * 1024;

    private readonly Channel<NotificationJob> channel;
    private long dropped;
    private long photoBytes;

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
                Release(job);
                log.LogWarning("คิวแจ้งเตือนเต็ม: ทิ้งข้อความเก่าสุด ({Event})", job.Event);
            });
    }

    /// <summary>Messages dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref dropped);

    /// <summary>The size of the pictures waiting now.</summary>
    public long PhotoBytes => Interlocked.Read(ref photoBytes);

    public ChannelReader<NotificationJob> Reader => channel.Reader;

    public bool TryWrite(NotificationJob job)
    {
        if (job.Photo is { } photo)
        {
            if (Interlocked.Add(ref photoBytes, photo.Length) > MaxQueuedPhotoBytes)
            {
                Interlocked.Add(ref photoBytes, -photo.Length);
                job = job with { Photo = null }; // the text still goes
            }
        }
        if (channel.Writer.TryWrite(job)) return true;
        Release(job);
        return false;
    }

    /// <summary>Takes the next message, if one is waiting.</summary>
    public bool TryRead(out NotificationJob job)
    {
        if (!channel.Reader.TryRead(out var read))
        {
            job = null!;
            return false;
        }
        job = read;
        Release(job);
        return true;
    }

    private void Release(NotificationJob job)
    {
        if (job.Photo is { } photo) Interlocked.Add(ref photoBytes, -photo.Length);
    }
}

/// <summary>Puts the message in the queue and returns at once; <see cref="NotificationWorker"/> sends it.</summary>
public sealed class QueuedNotificationDispatcher(NotificationQueue queue) : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default, byte[]? photo = null)
    {
        queue.TryWrite(new NotificationJob(ev, workspaceId, linkSetId, linkId, text, photo));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends inside the call that raised the event (<c>Notifications:Inline</c>). For the integration tests, which then know
/// what was sent as soon as their request returns. Not for production: a slow Telegram would slow the request.
/// </summary>
public sealed class InlineNotificationDispatcher(NotificationDelivery delivery) : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default, byte[]? photo = null) =>
        delivery.DeliverAsync(new NotificationJob(ev, workspaceId, linkSetId, linkId, text, photo), ct);
}
