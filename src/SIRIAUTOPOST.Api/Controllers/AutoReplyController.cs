using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.AutoReply;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// Auto-reply rules of a workspace (Pro and above). Stored only: nothing reads Facebook comments yet.
[ApiController]
[Route("api/workspaces/{wsId:guid}/auto-reply")]
[Produces("application/json")]
public sealed class AutoReplyController : ControllerBase
{
    [HttpGet]
    public Task<AutoReplyDto> Get(Guid wsId, [FromServices] IQueryHandler<GetAutoReplyQuery, AutoReplyDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetAutoReplyQuery(wsId), ct);

    /// <summary>Replaces the rules (403 below Pro; a rule's scope is "all" or the id of one of the workspace's collections).</summary>
    [HttpPut]
    public Task<AutoReplyDto> Update(
        Guid wsId, AutoReplyDto settings, [FromServices] ICommandHandler<UpdateAutoReplyCommand, AutoReplyDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateAutoReplyCommand(wsId, settings), ct);
}
