using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Backup;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Backup and restore of collections, link sets, schedules, the advanced anti-ban numbers, notification rules and
// auto-reply rules. Admins only. The file holds no secrets (no tokens, no chat ids).
[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class BackupController : ControllerBase
{
    [HttpGet("backup")]
    public Task<BackupDto> Backup(Guid wsId, [FromServices] IQueryHandler<GetBackupQuery, BackupDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetBackupQuery(wsId), ct);

    /// <summary>
    /// Replaces collections, link sets and schedules by the file's (400/422 with a Thai message and nothing changed when
    /// the file does not pass), deletes the replaced schedules' queued posts and queues the active schedules again.
    /// </summary>
    [HttpPost("restore")]
    [ProducesResponseType<RestoreResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<RestoreResultDto> Restore(
        Guid wsId, BackupDto backup, [FromServices] ICommandHandler<RestoreBackupCommand, RestoreResultDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new RestoreBackupCommand(wsId, backup), ct);
}
