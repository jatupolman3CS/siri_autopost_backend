using System.Diagnostics;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Api.Middlewares;

/// <summary>The last 1,000 API request durations, kept in memory (per process, reset on restart).</summary>
public sealed class RequestTimings : IRequestTimings
{
    private const int Size = 1000;
    private readonly double[] _ms = new double[Size];
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public void Record(TimeSpan duration)
    {
        lock (_gate)
        {
            _ms[_next] = duration.TotalMilliseconds;
            _next = (_next + 1) % Size;
            _count = Math.Min(_count + 1, Size);
        }
    }

    public (int? P95Ms, int Samples) Snapshot()
    {
        double[] copy;
        lock (_gate) copy = _ms[.._count];
        if (copy.Length == 0) return (null, 0);
        Array.Sort(copy);
        var at = (int)Math.Ceiling(copy.Length * 0.95) - 1;
        return ((int)Math.Round(copy[Math.Clamp(at, 0, copy.Length - 1)]), copy.Length);
    }
}

/// <summary>
/// Times every /api request for the admin overview's latency figure, except the ones that are held open on
/// purpose (the event stream lasts up to 30 minutes, a device's sync waits up to 25 seconds): they would be the
/// whole 95th percentile.
/// </summary>
public sealed class RequestTimingMiddleware(RequestDelegate next, IRequestTimings timings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/device/sync")
            || path.Value!.EndsWith("/events/stream", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            timings.Record(Stopwatch.GetElapsedTime(start));
        }
    }
}
