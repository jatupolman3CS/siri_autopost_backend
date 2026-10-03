using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Library;

public sealed record GetMediaQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<MediaDto>>;

public sealed class GetMediaQueryHandler(IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current)
    : IQueryHandler<GetMediaQuery, IReadOnlyList<MediaDto>>
{
    public async Task<IReadOnlyList<MediaDto>> HandleAsync(GetMediaQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(q.WorkspaceId, current, ct);
        return (await media.ListAsync(q.WorkspaceId, ct)).Select(MediaDto.From).ToList();
    }
}

public sealed record GetMediaContentQuery(Guid WorkspaceId, Guid MediaId) : IQuery<MediaContent>;

public sealed class GetMediaContentQueryHandler(IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current)
    : IQueryHandler<GetMediaContentQuery, MediaContent>
{
    public async Task<MediaContent> HandleAsync(GetMediaContentQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(q.WorkspaceId, current, ct);
        var file = await media.GetAsync(q.WorkspaceId, q.MediaId, ct) ?? throw new NotFoundException("ไฟล์", q.MediaId);
        return new MediaContent(file.Name, file.ContentType, file.Data);
    }
}

public sealed record UploadMediaCommand(Guid WorkspaceId, string FileName, string ContentType, byte[] Data) : ICommand<MediaDto>;

public sealed class UploadMediaCommandHandler(
    IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UploadMediaCommand, MediaDto>
{
    public async Task<MediaDto> HandleAsync(UploadMediaCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(c.WorkspaceId, current, ct);
        var file = MediaFile.Create(c.WorkspaceId, c.FileName, c.ContentType, c.Data, clock.GetUtcNow());
        media.Add(file);
        await uow.SaveChangesAsync(ct);
        return MediaDto.From(file);
    }
}

public sealed record GetSnippetsQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<SnippetDto>>;

public sealed class GetSnippetsQueryHandler(IWorkspaceRepository workspaces, ISnippetRepository snippets, ICurrentUser current)
    : IQueryHandler<GetSnippetsQuery, IReadOnlyList<SnippetDto>>
{
    public async Task<IReadOnlyList<SnippetDto>> HandleAsync(GetSnippetsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(q.WorkspaceId, current, ct);
        return (await snippets.ListAsync(q.WorkspaceId, ct)).Select(SnippetDto.From).ToList();
    }
}

public sealed record CreateSnippetCommand(Guid WorkspaceId, string Title, string Text) : ICommand<SnippetDto>;

public sealed class CreateSnippetCommandHandler(
    IWorkspaceRepository workspaces, ISnippetRepository snippets, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateSnippetCommand, SnippetDto>
{
    public async Task<SnippetDto> HandleAsync(CreateSnippetCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(c.WorkspaceId, current, ct);
        var snippet = Snippet.Create(c.WorkspaceId, c.Title, c.Text, clock.GetUtcNow());
        snippets.Add(snippet);
        await uow.SaveChangesAsync(ct);
        return SnippetDto.From(snippet);
    }
}
