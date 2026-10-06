using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Ai;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

// AI post drafts: the status tells the web app whether its AI buttons can work (key on the server, plan), and the
// drafts come back as text for the editor, never saved here.
[ApiController]
[Route("api/workspaces/{wsId:guid}/ai")]
[Produces("application/json")]
public sealed class AiController : ControllerBase
{
    /// <param name="Topic">What the post is about.</param>
    /// <param name="Points">Things the post must mention.</param>
    /// <param name="Tone">friendly, formal, sales or short.</param>
    /// <param name="Count">How many different drafts (1-5).</param>
    public sealed record WriteAiPostsRequest(string Topic, IReadOnlyList<string>? Points, string? Tone, int Count);

    [HttpGet("status")]
    public Task<AiStatusDto> Status(Guid wsId, [FromServices] IQueryHandler<GetAiStatusQuery, AiStatusDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetAiStatusQuery(wsId), ct);

    [HttpPost("posts")]
    public Task<AiDraftsDto> Posts(
        Guid wsId, WriteAiPostsRequest r, [FromServices] ICommandHandler<WriteAiPostsCommand, AiDraftsDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new WriteAiPostsCommand(wsId, r.Topic, r.Points, r.Tone, r.Count), ct);
}
