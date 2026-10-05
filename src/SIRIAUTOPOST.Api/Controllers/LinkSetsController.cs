using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.LinkSets;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Link sets ("ชุดลิงก์กลุ่ม"): Facebook group addresses with a group code and a daily cap. Viewers read, editors write.
[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class LinkSetsController : ControllerBase
{
    public sealed record CreateLinkSetRequest(string Name, Guid? PostAsAccountId);

    public sealed record SetActiveRequest(bool Active);
    public sealed record UpdateLinkSetRequest(string Name, Guid? PostAsAccountId, IReadOnlyList<Guid>? AccountIds);

    /// <summary>A blank or invalid address is allowed: the row is created invalid so the web app can add an empty row.</summary>
    public sealed record AddLinkRequest(string? Name, string? Url, string? Code, int? DailyMax);

    public sealed record UpdateLinkRequest(string? Name, string? Url, string? Code, int DailyMax, bool Enabled);

    public sealed record BulkLinksRequest(string Text);

    public sealed record ImportGroupsRequest(Guid AccountId, IReadOnlyList<string> Urls);

    public sealed record ImportCsvRequest(IReadOnlyList<CsvLinkRow> Rows);

    [HttpGet("link-sets")]
    public Task<IReadOnlyList<LinkSetDto>> List(
        Guid wsId, [FromServices] IQueryHandler<GetLinkSetsQuery, IReadOnlyList<LinkSetDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetLinkSetsQuery(wsId), ct);

    [HttpPost("link-sets")]
    public Task<LinkSetDto> Create(
        Guid wsId, CreateLinkSetRequest r, [FromServices] ICommandHandler<CreateLinkSetCommand, LinkSetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateLinkSetCommand(wsId, r.Name, r.PostAsAccountId), ct);

    [HttpPut("link-sets/{id:guid}")]
    public Task<LinkSetDto> Update(
        Guid wsId, Guid id, UpdateLinkSetRequest r, [FromServices] ICommandHandler<UpdateLinkSetCommand, LinkSetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateLinkSetCommand(wsId, id, r.Name, r.PostAsAccountId, r.AccountIds), ct);

    /// <summary>Deletes the set and its links; 422 while a schedule uses it.</summary>
    [HttpPut("link-sets/{id:guid}/active")]
    public Task<LinkSetDto> SetSetActive(
        Guid wsId, Guid id, SetActiveRequest r, [FromServices] ICommandHandler<SetLinkSetActiveCommand, LinkSetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetLinkSetActiveCommand(wsId, id, r.Active), ct);

    [HttpDelete("link-sets/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        Guid wsId, Guid id, [FromServices] ICommandHandler<DeleteLinkSetCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteLinkSetCommand(wsId, id), ct);
        return NoContent();
    }

    [HttpPost("link-sets/{id:guid}/links")]
    public Task<SetLinkDto> AddLink(
        Guid wsId, Guid id, AddLinkRequest r, [FromServices] ICommandHandler<AddLinkCommand, SetLinkDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new AddLinkCommand(wsId, id, r.Name, r.Url, r.Code, r.DailyMax), ct);

    [HttpPut("link-sets/{id:guid}/links/{linkId:guid}")]
    public Task<SetLinkDto> UpdateLink(
        Guid wsId, Guid id, Guid linkId, UpdateLinkRequest r, [FromServices] ICommandHandler<UpdateLinkCommand, SetLinkDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new UpdateLinkCommand(wsId, id, linkId, r.Name, r.Url, r.Code, r.DailyMax, r.Enabled), ct);

    [HttpDelete("link-sets/{id:guid}/links/{linkId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteLink(
        Guid wsId, Guid id, Guid linkId, [FromServices] ICommandHandler<DeleteLinkCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteLinkCommand(wsId, id, linkId), ct);
        return NoContent();
    }

    /// <summary>Switches a link on again (after the engine switched it off).</summary>
    [HttpPost("link-sets/{id:guid}/links/{linkId:guid}/enable")]
    public Task<SetLinkDto> EnableLink(
        Guid wsId, Guid id, Guid linkId, [FromServices] ICommandHandler<EnableLinkCommand, SetLinkDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new EnableLinkCommand(wsId, id, linkId), ct);

    /// <summary>Lines "address | code": blank lines are skipped, a known address counts as a duplicate (its code may be updated).</summary>
    [HttpPost("link-sets/{id:guid}/links/bulk")]
    public Task<BulkLinksResultDto> Bulk(
        Guid wsId, Guid id, BulkLinksRequest r, [FromServices] ICommandHandler<BulkAddLinksCommand, BulkLinksResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new BulkAddLinksCommand(wsId, id, r.Text), ct);

    /// <summary>Adds groups a connected account synced (skipping the ones the set has).</summary>
    [HttpPost("link-sets/{id:guid}/links/import")]
    public Task<LinkSetDto> ImportGroups(
        Guid wsId, Guid id, ImportGroupsRequest r, [FromServices] ICommandHandler<ImportAccountGroupsCommand, LinkSetDto> handler,
        CancellationToken ct) =>
        handler.HandleAsync(new ImportAccountGroupsCommand(wsId, id, r.AccountId, r.Urls), ct);

    /// <summary>Rows "set, name, address, code": missing sets are created by name; duplicates and invalid rows are skipped.</summary>
    [HttpPost("link-sets/import-csv")]
    public Task<CsvImportResultDto> ImportCsv(
        Guid wsId, ImportCsvRequest r, [FromServices] ICommandHandler<ImportLinksCsvCommand, CsvImportResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ImportLinksCsvCommand(wsId, r.Rows), ct);

    /// <summary>The groups the account's browser synced (what the "import groups" window lists).</summary>
    [HttpGet("accounts/{accountId:guid}/groups")]
    public Task<IReadOnlyList<GroupLinkDto>> AccountGroups(
        Guid wsId, Guid accountId, [FromServices] IQueryHandler<GetAccountGroupsQuery, IReadOnlyList<GroupLinkDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetAccountGroupsQuery(wsId, accountId), ct);
}
