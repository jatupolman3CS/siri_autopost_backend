using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Library;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class LibraryController : ControllerBase
{
    public sealed record CreateSnippetRequest(string Title, string Text);

    [HttpGet("media")]
    public Task<IReadOnlyList<MediaDto>> Media(
        Guid wsId, [FromServices] IQueryHandler<GetMediaQuery, IReadOnlyList<MediaDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetMediaQuery(wsId), ct);

    /// <summary>Multipart upload of one image or video (field "file").</summary>
    [HttpPost("media")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MediaFile.MaxBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MediaFile.MaxBytes + 1024 * 1024)]
    public async Task<MediaDto> Upload(
        Guid wsId, IFormFile file, [FromServices] ICommandHandler<UploadMediaCommand, MediaDto> handler, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await handler.HandleAsync(new UploadMediaCommand(wsId, file.FileName, file.ContentType, ms.ToArray()), ct);
    }

    [HttpGet("media/{mediaId:guid}/content")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Content(
        Guid wsId, Guid mediaId, [FromServices] IQueryHandler<GetMediaContentQuery, MediaContent> handler, [FromServices] IHttpClientFactory http, CancellationToken ct)
    {
        var m = await handler.HandleAsync(new GetMediaContentQuery(wsId, mediaId), ct);
        return await this.ToResultAsync(m, http, ct);
    }

    [HttpGet("snippets")]
    public Task<IReadOnlyList<SnippetDto>> Snippets(
        Guid wsId, [FromServices] IQueryHandler<GetSnippetsQuery, IReadOnlyList<SnippetDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetSnippetsQuery(wsId), ct);

    [HttpPost("snippets")]
    public Task<SnippetDto> CreateSnippet(
        Guid wsId, CreateSnippetRequest r, [FromServices] ICommandHandler<CreateSnippetCommand, SnippetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateSnippetCommand(wsId, r.Title, r.Text), ct);
}
