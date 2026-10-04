using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Library;

public sealed record GetMediaFoldersQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<MediaFolderDto>>;

public sealed class GetMediaFoldersQueryHandler(IWorkspaceRepository workspaces, IMediaFolderRepository folders, ICurrentUser current)
    : IQueryHandler<GetMediaFoldersQuery, IReadOnlyList<MediaFolderDto>>
{
    public async Task<IReadOnlyList<MediaFolderDto>> HandleAsync(GetMediaFoldersQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return (await folders.ListAsync(q.WorkspaceId, ct)).Select(MediaFolderDto.From).ToList();
    }
}

internal static class FolderNames
{
    /// <summary>A name is unique inside the workspace, ignoring case and the folder being renamed.</summary>
    public static async Task EnsureFreeAsync(IMediaFolderRepository folders, Guid workspaceId, string? name, Guid? self, CancellationToken ct)
    {
        var n = (name ?? "").Trim();
        var all = await folders.ListAsync(workspaceId, ct);
        if (all.Any(f => f.Id != self && string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("มีโฟลเดอร์ชื่อนี้แล้ว");
        if (self is null && all.Count >= MediaFolder.MaxPerWorkspace)
            throw new DomainException($"สร้างโฟลเดอร์ได้ไม่เกิน {MediaFolder.MaxPerWorkspace} โฟลเดอร์");
    }
}

public sealed record CreateMediaFolderCommand(Guid WorkspaceId, string Name) : ICommand<MediaFolderDto>;

public sealed class CreateMediaFolderCommandHandler(
    IWorkspaceRepository workspaces, IMediaFolderRepository folders, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateMediaFolderCommand, MediaFolderDto>
{
    public async Task<MediaFolderDto> HandleAsync(CreateMediaFolderCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var folder = MediaFolder.Create(c.WorkspaceId, c.Name, clock.GetUtcNow());
        await FolderNames.EnsureFreeAsync(folders, c.WorkspaceId, folder.Name, null, ct);
        folders.Add(folder);
        await uow.SaveChangesAsync(ct);
        return MediaFolderDto.From(folder);
    }
}

public sealed record RenameMediaFolderCommand(Guid WorkspaceId, Guid FolderId, string Name) : ICommand<MediaFolderDto>;

public sealed class RenameMediaFolderCommandHandler(
    IWorkspaceRepository workspaces, IMediaFolderRepository folders, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<RenameMediaFolderCommand, MediaFolderDto>
{
    public async Task<MediaFolderDto> HandleAsync(RenameMediaFolderCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var folder = await folders.GetForUpdateAsync(c.WorkspaceId, c.FolderId, ct) ?? throw new NotFoundException("โฟลเดอร์", c.FolderId);
        folder.Rename(c.Name);
        await FolderNames.EnsureFreeAsync(folders, c.WorkspaceId, folder.Name, folder.Id, ct);
        await uow.SaveChangesAsync(ct);
        return MediaFolderDto.From(folder);
    }
}

/// <summary>Deletes the folder only; its files go back to "no folder" and stay in the library.</summary>
public sealed record DeleteMediaFolderCommand(Guid WorkspaceId, Guid FolderId) : ICommand<Unit>;

public sealed class DeleteMediaFolderCommandHandler(
    IWorkspaceRepository workspaces, IMediaFolderRepository folders, IMediaRepository media, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeleteMediaFolderCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteMediaFolderCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var folder = await folders.GetForUpdateAsync(c.WorkspaceId, c.FolderId, ct) ?? throw new NotFoundException("โฟลเดอร์", c.FolderId);
        await media.ClearFolderAsync(c.WorkspaceId, folder.Id, ct);
        folders.Remove(folder);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>Puts files into a folder, or out of every folder when <c>FolderId</c> is null.</summary>
public sealed record MoveMediaCommand(Guid WorkspaceId, IReadOnlyList<Guid> MediaIds, Guid? FolderId) : ICommand<IReadOnlyList<MediaDto>>;

public sealed class MoveMediaCommandHandler(
    IWorkspaceRepository workspaces, IMediaRepository media, IMediaFolderRepository folders, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<MoveMediaCommand, IReadOnlyList<MediaDto>>
{
    public const int MaxPerCall = 500;

    public async Task<IReadOnlyList<MediaDto>> HandleAsync(MoveMediaCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        if (c.MediaIds.Count == 0) return [];
        if (c.MediaIds.Count > MaxPerCall) throw new DomainException($"ย้ายได้ครั้งละไม่เกิน {MaxPerCall} ไฟล์");
        if (c.FolderId is { } fid && await folders.GetAsync(c.WorkspaceId, fid, ct) is null) throw new NotFoundException("โฟลเดอร์", fid);
        var files = await media.GetManyForUpdateAsync(c.WorkspaceId, c.MediaIds, ct);
        foreach (var f in files) f.MoveToFolder(c.FolderId);
        await uow.SaveChangesAsync(ct);
        return files.Select(MediaDto.From).ToList();
    }
}
