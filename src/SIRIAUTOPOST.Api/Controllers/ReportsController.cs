using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Reports;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// How groups and posts did over the last 7 or 30 days (viewers), and client report links (Agency, admins).
[ApiController]
[Route("api/workspaces/{wsId:guid}/reports")]
[Produces("application/json")]
public sealed class ReportsController : ControllerBase
{
    /// <summary>Real posts only (accounts with a device, no tests). <paramref name="days"/> is 7 or 30; anything else is a 400.</summary>
    [HttpGet]
    public Task<ReportDto> Get(
        Guid wsId, [FromServices] IQueryHandler<GetReportQuery, ReportDto> handler, CancellationToken ct, [FromQuery] int days = 7) =>
        handler.HandleAsync(new GetReportQuery(wsId, days), ct);

    /// <summary>Freezes the report behind a link that lives 30 days (403 below Agency; 422 beyond 20 live links).</summary>
    [HttpPost("share")]
    public Task<ReportShareDto> Share(
        Guid wsId, ShareReportRequest r, [FromServices] ICommandHandler<ShareReportCommand, ReportShareDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ShareReportCommand(wsId, r.Brand, r.Period, r.Logo), ct);
}

// The page a client opens from a shared link: no sign-in, the random token is the key.
[ApiController]
[AllowAnonymous]
[Route("api/reports/shared")]
[Produces("application/json")]
public sealed class SharedReportsController : ControllerBase
{
    /// <summary>The frozen report, or 404 when the link is unknown or expired. Never cached.</summary>
    [HttpGet("{token}")]
    public Task<SharedReportDto> Get(string token, [FromServices] IQueryHandler<GetSharedReportQuery, SharedReportDto> handler, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return handler.HandleAsync(new GetSharedReportQuery(token), ct);
    }
}
