using System.Collections.Concurrent;
using System.Threading.Channels;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Infrastructure.Services;

/// <summary>
/// In-process fan-out of saved <see cref="DeviceEvent"/>s (one API instance). Every web stream has its own
/// bounded channel: a stream that cannot keep up loses the oldest events and reads them back by Seq, so a slow
/// browser never grows the API's memory. Device waits are one-shot: the first matching event releases them.
/// When the host stops, every subscription completes so clients reconnect to the next instance.
/// </summary>
public sealed class DeviceEventBus : IDeviceEventBus, IDisposable
{
    /// <summary>Events buffered per web stream before the oldest are dropped.</summary>
    public const int StreamBuffer = 256;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Subscription, byte>> _byWorkspace = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Waiter, byte>> _byDevice = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _streams;
    private int _waits;
    private long _published;
    private long _dropped;

    public CancellationToken Stopping => _stopping.Token;

    public DeviceEventBusStats Stats =>
        new(Volatile.Read(ref _streams), Volatile.Read(ref _waits), Interlocked.Read(ref _published), Interlocked.Read(ref _dropped));

    public void Publish(IReadOnlyList<DeviceEvent> events)
    {
        if (events.Count == 0) return;
        Interlocked.Add(ref _published, events.Count);
        foreach (var e in events)
        {
            if (_byWorkspace.TryGetValue(e.WorkspaceId, out var subs))
                foreach (var s in subs.Keys)
                    if (!s.Offer(e)) Interlocked.Increment(ref _dropped);
            if (_byDevice.TryGetValue(e.DeviceId, out var waiters))
                foreach (var w in waiters.Keys)
                    w.Offer(e);
        }
    }

    public IDeviceEventSubscription Subscribe(Guid workspaceId)
    {
        var sub = new Subscription(this, workspaceId);
        _byWorkspace.GetOrAdd(workspaceId, _ => new())[sub] = 0;
        Interlocked.Increment(ref _streams);
        if (_stopping.IsCancellationRequested) sub.Complete();
        return sub;
    }

    public async Task<bool> WaitForAsync(Guid deviceId, string type, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_stopping.IsCancellationRequested || timeout <= TimeSpan.Zero) return false;
        var waiter = new Waiter(type);
        var set = _byDevice.GetOrAdd(deviceId, _ => new());
        set[waiter] = 0;
        Interlocked.Increment(ref _waits);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            linked.CancelAfter(timeout);
            try
            {
                return await waiter.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                return false; // timeout, caller gone, or shutting down: the caller falls back to polling
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waits);
            set.TryRemove(waiter, out _);
            if (set.IsEmpty) _byDevice.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<Waiter, byte>>(deviceId, set));
        }
    }

    private void Remove(Subscription sub)
    {
        if (_byWorkspace.TryGetValue(sub.WorkspaceId, out var subs) && subs.TryRemove(sub, out _))
        {
            Interlocked.Decrement(ref _streams);
            if (subs.IsEmpty) _byWorkspace.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<Subscription, byte>>(sub.WorkspaceId, subs));
        }
    }

    /// <summary>Ends every stream and wait; new subscriptions complete at once.</summary>
    public void Stop()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        foreach (var subs in _byWorkspace.Values)
            foreach (var s in subs.Keys) s.Complete();
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
    }

    private sealed class Subscription : IDeviceEventSubscription
    {
        private readonly DeviceEventBus _bus;
        private readonly Channel<DeviceEvent> _channel = Channel.CreateBounded<DeviceEvent>(
            new BoundedChannelOptions(StreamBuffer) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        public Subscription(DeviceEventBus bus, Guid workspaceId)
        {
            _bus = bus;
            WorkspaceId = workspaceId;
        }

        public Guid WorkspaceId { get; }
        public ChannelReader<DeviceEvent> Events => _channel.Reader;

        /// <summary>False when the buffer was full and an older event made room (DropOldest never refuses).</summary>
        public bool Offer(DeviceEvent e)
        {
            var hadRoom = _channel.Reader.Count < StreamBuffer;
            _channel.Writer.TryWrite(e);
            return hadRoom;
        }

        public void Complete() => _channel.Writer.TryComplete();

        public void Dispose()
        {
            Complete();
            _bus.Remove(this);
        }
    }

    private sealed class Waiter(string type)
    {
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Task => _tcs.Task;

        public void Offer(DeviceEvent e)
        {
            if (e.Type == type) _tcs.TrySetResult(true);
        }
    }
}
