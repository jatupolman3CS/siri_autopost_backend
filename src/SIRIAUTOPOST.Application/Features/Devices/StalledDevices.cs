using System.Collections.Concurrent;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Devices;

/// <summary>
/// The devices <see cref="CheckStalledDevicesCommandHandler"/> has already told about, so one stall is one message. Kept in
/// memory: after a restart a device that is still stalled is told about once more, which is a fair reminder.
/// </summary>
public sealed class StalledDeviceTracker
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> flagged = new();

    /// <summary>Marks a device as told about (with when it was last seen); false when it already was.</summary>
    public bool TryFlag(Guid deviceId, DateTimeOffset lastSeen) => flagged.TryAdd(deviceId, lastSeen);

    public IReadOnlyList<KeyValuePair<Guid, DateTimeOffset>> Flagged() => flagged.ToArray();

    public void Clear(Guid deviceId) => flagged.TryRemove(deviceId, out _);
}

/// <summary>
/// Looks for jobs that stand still because their machine is silent: a device that has not called in for
/// <see cref="CheckStalledDevicesCommandHandler.StallAfter"/> while posts for it are due (the <see cref="NotifyEvent.Offline"/>
/// event), and tells again when it is back. A machine that is off with nothing to do is not a stall and says nothing, and
/// a device the web paused waits on purpose. Returns how many messages it raised. Run every minute by the host.
/// </summary>
public sealed record CheckStalledDevicesCommand : ICommand<int>;

public sealed class CheckStalledDevicesCommandHandler(
    IPostRepository posts, IDeviceRepository devices, IWorkspaceRepository workspaces, INotificationDispatcher notifier,
    StalledDeviceTracker tracker, TimeProvider clock)
    : ICommandHandler<CheckStalledDevicesCommand, int>
{
    /// <summary>Silent for this long, with a post due for that long, counts as stalled (a blip or a slow poll does not).</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(5);

    public async Task<int> HandleAsync(CheckStalledDevicesCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var sent = 0;
        var stalled = await posts.ListStalledDevicesAsync(now - StallAfter, now - StallAfter, ct);
        foreach (var s in stalled)
        {
            var device = await devices.GetByIdAsync(s.DeviceId, ct);
            if (device is null) continue;
            var lastSeen = device.LastSeenAt ?? device.CreatedAt;
            if (!tracker.TryFlag(device.Id, lastSeen)) continue; // told already
            var ws = await workspaces.GetByIdAsync(s.WorkspaceId, ct);
            if (ws is null || !ws.Notifications.Resolve(NotifyEvent.Offline, null, null).Any) continue;
            await EngineNotices.SendAsync(
                notifier, ws.Id, [EngineNotices.DeviceStalled(device, now - lastSeen, s.Due, s.Oldest, NotificationText.DefaultUtcOffsetMinutes)], ct);
            sent++;
        }

        // Told about and no longer stalled: the device called in again (or its posts are gone: nothing to say then).
        var stillStalled = stalled.Select(s => s.DeviceId).ToHashSet();
        foreach (var (id, lastSeen) in tracker.Flagged())
        {
            if (stillStalled.Contains(id)) continue;
            tracker.Clear(id);
            var device = await devices.GetByIdAsync(id, ct);
            if (device?.LastSeenAt is not { } seen || now - seen > StallAfter) continue;
            var ws = await workspaces.GetByIdAsync(device.WorkspaceId, ct);
            if (ws is null || !ws.Notifications.Resolve(NotifyEvent.Offline, null, null).Any) continue;
            await EngineNotices.SendAsync(notifier, ws.Id, [EngineNotices.DeviceBack(device, now - lastSeen)], ct);
            sent++;
        }
        return sent;
    }
}
