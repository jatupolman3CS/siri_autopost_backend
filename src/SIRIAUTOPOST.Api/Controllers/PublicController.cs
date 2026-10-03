using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Public;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>Figures anyone may see (the landing page): counts only, never a customer's content.</summary>
[ApiController]
[Route("api/public")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class PublicController : ControllerBase
{
    /// <summary>
    /// Posts sent in the last 7 days, the success rate and the devices active in 24 hours. Cached for
    /// <c>Public:StatsCacheSeconds</c> (60 by default, 0 = not cached) so a busy landing page costs one query a minute.
    /// </summary>
    [HttpGet("stats")]
    public async Task<PublicStatsDto> Stats(
        [FromServices] IQueryHandler<GetPublicStatsQuery, PublicStatsDto> handler, [FromServices] IMemoryCache cache,
        [FromServices] IConfiguration config, CancellationToken ct)
    {
        var seconds = config.GetValue("Public:StatsCacheSeconds", 60);
        if (seconds <= 0) return await handler.HandleAsync(new GetPublicStatsQuery(), ct);
        return (await cache.GetOrCreateAsync("public-stats", e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds);
            return handler.HandleAsync(new GetPublicStatsQuery(), ct);
        }))!;
    }
}
