using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Library;

public sealed record GetMediaQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<MediaDto>>;

public sealed class GetMediaQueryHandler(IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current)
    : IQueryHandler<GetMediaQuery, IReadOnlyList<MediaDto>>
{
    public async Task<IReadOnlyList<MediaDto>> HandleAsync(GetMediaQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return (await media.ListAsync(q.WorkspaceId, ct)).Select(MediaDto.From).ToList();
    }
}

public sealed record GetMediaContentQuery(Guid WorkspaceId, Guid MediaId) : IQuery<MediaContent>;

public sealed class GetMediaContentQueryHandler(IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current)
    : IQueryHandler<GetMediaContentQuery, MediaContent>
{
    public async Task<MediaContent> HandleAsync(GetMediaContentQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var file = await media.GetAsync(q.WorkspaceId, q.MediaId, ct) ?? throw new NotFoundException("ไฟล์", q.MediaId);
        return new MediaContent(file.Name, file.ContentType, file.Data, file.ExternalUrl);
    }
}

public sealed record UploadMediaCommand(Guid WorkspaceId, string FileName, string ContentType, byte[] Data, Guid? FolderId = null) : ICommand<MediaDto>;

public sealed class UploadMediaCommandHandler(
    IWorkspaceRepository workspaces, IMediaRepository media, IMediaFolderRepository folders, PlanQuotas quotas, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UploadMediaCommand, MediaDto>
{
    public async Task<MediaDto> HandleAsync(UploadMediaCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        await quotas.EnsureImagesAsync(c.WorkspaceId, 1, ct);
        var file = MediaFile.Create(c.WorkspaceId, c.FileName, c.ContentType, c.Data, clock.GetUtcNow());
        if (c.FolderId is { } fid)
        {
            if (await folders.GetAsync(c.WorkspaceId, fid, ct) is null) throw new NotFoundException("โฟลเดอร์", fid);
            file.MoveToFolder(fid);
        }
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
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
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
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var snippet = Snippet.Create(c.WorkspaceId, c.Title, c.Text, clock.GetUtcNow());
        snippets.Add(snippet);
        await uow.SaveChangesAsync(ct);
        return SnippetDto.From(snippet);
    }
}

public sealed record UpdateSnippetCommand(Guid WorkspaceId, Guid SnippetId, string Title, string Text) : ICommand<SnippetDto>;

public sealed class UpdateSnippetCommandHandler(
    IWorkspaceRepository workspaces, ISnippetRepository snippets, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<UpdateSnippetCommand, SnippetDto>
{
    public async Task<SnippetDto> HandleAsync(UpdateSnippetCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var snippet = await snippets.GetForUpdateAsync(c.WorkspaceId, c.SnippetId, ct) ?? throw new NotFoundException("ข้อความ", c.SnippetId);
        snippet.Update(c.Title, c.Text);
        await uow.SaveChangesAsync(ct);
        return SnippetDto.From(snippet);
    }
}

public sealed record SetSnippetActiveCommand(Guid WorkspaceId, Guid SnippetId, bool Active) : ICommand<SnippetDto>;

public sealed class SetSnippetActiveCommandHandler(
    IWorkspaceRepository workspaces, ISnippetRepository snippets, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<SetSnippetActiveCommand, SnippetDto>
{
    public async Task<SnippetDto> HandleAsync(SetSnippetActiveCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var snippet = await snippets.GetForUpdateAsync(c.WorkspaceId, c.SnippetId, ct) ?? throw new NotFoundException("ข้อความ", c.SnippetId);
        snippet.SetActive(c.Active);
        await uow.SaveChangesAsync(ct);
        return SnippetDto.From(snippet);
    }
}

public sealed record DeleteSnippetCommand(Guid WorkspaceId, Guid SnippetId) : ICommand<Unit>;

public sealed class DeleteSnippetCommandHandler(
    IWorkspaceRepository workspaces, ISnippetRepository snippets, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeleteSnippetCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteSnippetCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var snippet = await snippets.GetForUpdateAsync(c.WorkspaceId, c.SnippetId, ct) ?? throw new NotFoundException("ข้อความ", c.SnippetId);
        snippets.Remove(snippet);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record RenameMediaCommand(Guid WorkspaceId, Guid MediaId, string Name) : ICommand<MediaDto>;

public sealed class RenameMediaCommandHandler(
    IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<RenameMediaCommand, MediaDto>
{
    public async Task<MediaDto> HandleAsync(RenameMediaCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var file = (await media.GetManyForUpdateAsync(c.WorkspaceId, [c.MediaId], ct)).FirstOrDefault() ?? throw new NotFoundException("ไฟล์", c.MediaId);
        file.Rename(c.Name);
        await uow.SaveChangesAsync(ct);
        return MediaDto.From(file);
    }
}

/// <summary>Switches files on or off: an off file stays in the library but is not offered for new posts.</summary>
public sealed record SetMediaActiveCommand(Guid WorkspaceId, IReadOnlyList<Guid> MediaIds, bool Active) : ICommand<IReadOnlyList<MediaDto>>;

public sealed class SetMediaActiveCommandHandler(
    IWorkspaceRepository workspaces, IMediaRepository media, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<SetMediaActiveCommand, IReadOnlyList<MediaDto>>
{
    public async Task<IReadOnlyList<MediaDto>> HandleAsync(SetMediaActiveCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        if (c.MediaIds.Count == 0) return [];
        if (c.MediaIds.Count > MoveMediaCommandHandler.MaxPerCall)
            throw new DomainException($"ทำรายการได้ครั้งละไม่เกิน {MoveMediaCommandHandler.MaxPerCall} ไฟล์");
        var files = await media.GetManyForUpdateAsync(c.WorkspaceId, c.MediaIds, ct);
        if (files.Count == 0) throw new NotFoundException("ไฟล์", c.MediaIds[0]);
        foreach (var f in files) f.SetActive(c.Active);
        await uow.SaveChangesAsync(ct);
        return files.Select(MediaDto.From).ToList();
    }
}

/// <summary>
/// Deletes files from the library. Collection posts that used them lose the attachment (their text stays); posts that
/// are already queued go out without it. Ids that are not in the workspace are ignored.
/// </summary>
public sealed record DeleteMediaCommand(Guid WorkspaceId, IReadOnlyList<Guid> MediaIds) : ICommand<int>;

public sealed class DeleteMediaCommandHandler(
    IWorkspaceRepository workspaces, IMediaRepository media, ICollectionPostRepository collectionPosts, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeleteMediaCommand, int>
{
    public async Task<int> HandleAsync(DeleteMediaCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var ids = c.MediaIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        if (ids.Count > MoveMediaCommandHandler.MaxPerCall)
            throw new DomainException($"ลบได้ครั้งละไม่เกิน {MoveMediaCommandHandler.MaxPerCall} ไฟล์");
        var gone = ids.ToHashSet();
        foreach (var post in await collectionPosts.ListForUpdateAsync(c.WorkspaceId, ct)) post.DropMedia(gone);
        await uow.SaveChangesAsync(ct);
        return await media.RemoveManyAsync(c.WorkspaceId, ids, ct);
    }
}
