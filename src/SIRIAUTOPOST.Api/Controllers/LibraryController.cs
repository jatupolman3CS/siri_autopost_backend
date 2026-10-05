using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Library;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/workspaces/{wsId:guid}")]
[Produces("application/json")]
public sealed class LibraryController : ControllerBase
{
    public sealed record CreateSnippetRequest(string Title, string Text);
    public sealed record FolderRequest(string Name);
    public sealed record MoveMediaRequest(IReadOnlyList<Guid> MediaIds, Guid? FolderId);
    public sealed record UpdateSnippetRequest(string Title, string Text);
    public sealed record ActiveRequest(bool Active);
    public sealed record MediaNameRequest(string Name);
    public sealed record MediaIdsRequest(IReadOnlyList<Guid> MediaIds);
    public sealed record MediaActiveRequest(IReadOnlyList<Guid> MediaIds, bool Active);

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
        Guid wsId, IFormFile file, [FromForm] Guid? folderId, [FromServices] ICommandHandler<UploadMediaCommand, MediaDto> handler, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await handler.HandleAsync(new UploadMediaCommand(wsId, file.FileName, file.ContentType, ms.ToArray(), folderId), ct);
    }

    [HttpGet("media/{mediaId:guid}/content")]
    [Produces("application/octet-stream")]
    public async Task<IActionResult> Content(
        Guid wsId, Guid mediaId, [FromServices] IQueryHandler<GetMediaContentQuery, MediaContent> handler, [FromServices] IHttpClientFactory http, [FromServices] IObjectStorage storage, CancellationToken ct)
    {
        var m = await handler.HandleAsync(new GetMediaContentQuery(wsId, mediaId), ct);
        return await this.ToResultAsync(m, http, storage, ct);
    }

    [HttpGet("media-folders")]
    public Task<IReadOnlyList<MediaFolderDto>> Folders(
        Guid wsId, [FromServices] IQueryHandler<GetMediaFoldersQuery, IReadOnlyList<MediaFolderDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetMediaFoldersQuery(wsId), ct);

    [HttpPost("media-folders")]
    public Task<MediaFolderDto> CreateFolder(
        Guid wsId, FolderRequest r, [FromServices] ICommandHandler<CreateMediaFolderCommand, MediaFolderDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateMediaFolderCommand(wsId, r.Name), ct);

    [HttpPut("media-folders/{folderId:guid}")]
    public Task<MediaFolderDto> RenameFolder(
        Guid wsId, Guid folderId, FolderRequest r, [FromServices] ICommandHandler<RenameMediaFolderCommand, MediaFolderDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new RenameMediaFolderCommand(wsId, folderId, r.Name), ct);

    [HttpDelete("media-folders/{folderId:guid}")]
    public async Task<IActionResult> DeleteFolder(
        Guid wsId, Guid folderId, [FromServices] ICommandHandler<DeleteMediaFolderCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteMediaFolderCommand(wsId, folderId), ct);
        return NoContent();
    }

    /// <summary>Moves files into a folder (or out of every folder with a null folderId).</summary>
    [HttpPost("media/move")]
    public Task<IReadOnlyList<MediaDto>> Move(
        Guid wsId, MoveMediaRequest r, [FromServices] ICommandHandler<MoveMediaCommand, IReadOnlyList<MediaDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new MoveMediaCommand(wsId, r.MediaIds, r.FolderId), ct);

    [HttpGet("snippets")]
    public Task<IReadOnlyList<SnippetDto>> Snippets(
        Guid wsId, [FromServices] IQueryHandler<GetSnippetsQuery, IReadOnlyList<SnippetDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetSnippetsQuery(wsId), ct);

    [HttpPost("snippets")]
    public Task<SnippetDto> CreateSnippet(
        Guid wsId, CreateSnippetRequest r, [FromServices] ICommandHandler<CreateSnippetCommand, SnippetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateSnippetCommand(wsId, r.Title, r.Text), ct);

    [HttpPut("snippets/{snippetId:guid}")]
    public Task<SnippetDto> UpdateSnippet(
        Guid wsId, Guid snippetId, UpdateSnippetRequest r, [FromServices] ICommandHandler<UpdateSnippetCommand, SnippetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new UpdateSnippetCommand(wsId, snippetId, r.Title, r.Text), ct);

    [HttpPut("snippets/{snippetId:guid}/active")]
    public Task<SnippetDto> SetSnippetActive(
        Guid wsId, Guid snippetId, ActiveRequest r, [FromServices] ICommandHandler<SetSnippetActiveCommand, SnippetDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetSnippetActiveCommand(wsId, snippetId, r.Active), ct);

    [HttpDelete("snippets/{snippetId:guid}")]
    public async Task<IActionResult> DeleteSnippet(
        Guid wsId, Guid snippetId, [FromServices] ICommandHandler<DeleteSnippetCommand, Unit> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteSnippetCommand(wsId, snippetId), ct);
        return NoContent();
    }

    [HttpPut("media/{mediaId:guid}")]
    public Task<MediaDto> RenameMedia(
        Guid wsId, Guid mediaId, MediaNameRequest r, [FromServices] ICommandHandler<RenameMediaCommand, MediaDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new RenameMediaCommand(wsId, mediaId, r.Name), ct);

    /// <summary>Switches files on or off (off = kept, but not offered for new posts).</summary>
    [HttpPost("media/active")]
    public Task<IReadOnlyList<MediaDto>> SetMediaActive(
        Guid wsId, MediaActiveRequest r, [FromServices] ICommandHandler<SetMediaActiveCommand, IReadOnlyList<MediaDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new SetMediaActiveCommand(wsId, r.MediaIds, r.Active), ct);

    /// <summary>Deletes files from the library (at most 500 at a time).</summary>
    [HttpPost("media/delete")]
    public async Task<IActionResult> DeleteMedia(
        Guid wsId, MediaIdsRequest r, [FromServices] ICommandHandler<DeleteMediaCommand, int> handler, CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteMediaCommand(wsId, r.MediaIds), ct);
        return NoContent();
    }
}
