using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Features.Public;

/// <summary>
/// The landing page's figures: platform-wide counts of the last 7 days (Thai calendar), from posts of accounts
/// connected through a paired browser. No customer's content, only numbers.
/// </summary>
public sealed record GetPublicStatsQuery : IQuery<PublicStatsDto>;

public sealed class GetPublicStatsQueryHandler(IPostRepository posts, IDeviceRepository devices, TimeProvider clock)
    : IQueryHandler<GetPublicStatsQuery, PublicStatsDto>
{
    private static readonly TimeSpan Thai = TimeSpan.FromHours(7);

    public async Task<PublicStatsDto> HandleAsync(GetPublicStatsQuery q, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var today = now.ToOffset(Thai).Date;
        var firstDay = today.AddDays(-6);
        var from = new DateTimeOffset(firstDay, Thai);
        var finished = await posts.ListFinishedAsync(from.ToUniversalTime(), now, ct); // Npgsql takes UTC only

        static bool Sent(PostStatus s) => s is PostStatus.Success or PostStatus.Pending;
        var days = Enumerable.Range(0, 7).Select(i => firstDay.AddDays(i)).Select(d =>
        {
            var day = finished.Where(f => f.At.ToOffset(Thai).Date == d).ToList();
            return new PublicDayDto(d.ToString("yyyy-MM-dd"), day.Count(f => Sent(f.Status)), day.Count(f => f.Status == PostStatus.Failed));
        }).ToList();
        var sent = days.Sum(d => d.Sent);
        var failed = days.Sum(d => d.Failed);
        return new PublicStatsDto(sent, failed, PlatformMetrics.SuccessRate(sent, failed), await devices.CountSeenSinceAsync(now.AddDays(-1), ct), days);
    }
}
