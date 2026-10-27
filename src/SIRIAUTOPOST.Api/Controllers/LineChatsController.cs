using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.LineChats;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/workspaces/{wsId:guid}/line/chats")]
[Produces("application/json")]
public sealed class LineChatsController : ControllerBase
{
    [HttpGet]
    public Task<LineChatPageDto> GetLineChats(
        Guid wsId,
        [FromQuery] Guid? accountId,
        [FromQuery] string? q,
        [FromQuery] string? label,
        [FromQuery] int? activeWithinDays,
        [FromQuery] bool? doNotSend,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromServices] IQueryHandler<GetLineChatsQuery, LineChatPageDto> handler = null!,
        CancellationToken ct = default) =>
        handler.HandleAsync(new GetLineChatsQuery(wsId, accountId, q, label, activeWithinDays, doNotSend, page, pageSize), ct);

    [HttpPut("{id:guid}")]
    public Task<LineChatDto> UpdateLineChat(
        Guid wsId,
        Guid id,
        [FromBody] UpdateLineChatRequest r,
        [FromServices] ICommandHandler<UpdateLineChatCommand, LineChatDto> handler = null!,
        CancellationToken ct = default) =>
        handler.HandleAsync(new UpdateLineChatCommand(wsId, id, r.DoNotSend), ct);
}
