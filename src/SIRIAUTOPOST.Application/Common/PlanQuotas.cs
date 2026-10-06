using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// The package's size limits for what a workspace OWNER keeps: groups and pages (links of the link sets), files of
/// the image library and posts of the post library, each counted over all the owner's workspaces (the same rule as
/// the posts per day and the accounts). Called by the handlers that add such things, with how many are being added;
/// a plan without a limit (null) never refuses. The message says what the plan allows and how much is used.
/// </summary>
public sealed class PlanQuotas(
    IWorkspaceRepository workspaces, IUserRepository users, IPlanRepository plans, ISetLinkRepository links, IMediaRepository media,
    ICollectionPostRepository posts)
{
    public async Task EnsureGroupsAsync(Guid workspaceId, int adding, CancellationToken ct)
    {
        if (adding <= 0) return;
        var (limits, own) = await ContextAsync(workspaceId, ct);
        if (limits.Groups is not { } max) return;
        var used = await links.CountByWorkspacesAsync(own, ct);
        if (used + adding > max)
            throw new DomainException($"แผนปัจจุบันเก็บกลุ่ม/เพจได้สูงสุด {max:N0} (ใช้ไปแล้ว {used:N0}) ลบกลุ่มที่ไม่ใช้หรืออัปเกรดแผนก่อน");
    }

    public async Task EnsureImagesAsync(Guid workspaceId, int adding, CancellationToken ct)
    {
        if (adding <= 0) return;
        var (limits, own) = await ContextAsync(workspaceId, ct);
        if (limits.Images is not { } max) return;
        var used = await media.CountByWorkspacesAsync(own, ct);
        if (used + adding > max)
            throw new DomainException($"แผนปัจจุบันเก็บไฟล์ในคลังรูปได้สูงสุด {max:N0} ไฟล์ (ใช้ไปแล้ว {used:N0}) ลบไฟล์ที่ไม่ใช้หรืออัปเกรดแผนก่อน");
    }

    public async Task EnsureLibraryPostsAsync(Guid workspaceId, int adding, CancellationToken ct)
    {
        if (adding <= 0) return;
        var (limits, own) = await ContextAsync(workspaceId, ct);
        if (limits.LibraryPosts is not { } max) return;
        var used = await posts.CountByWorkspacesAsync(own, ct);
        if (used + adding > max)
            throw new DomainException($"แผนปัจจุบันเก็บโพสต์ในคลังโพสต์ได้สูงสุด {max:N0} โพสต์ (ใช้ไปแล้ว {used:N0}) ลบโพสต์ที่ไม่ใช้หรืออัปเกรดแผนก่อน");
    }

    /// <summary>
    /// A restore replaces the workspace's links and posts with the file's: what is left of the owner's quota is what the
    /// owner's OTHER workspaces do not use.
    /// </summary>
    public async Task EnsureRestoreFitsAsync(Guid workspaceId, int linkCount, int libraryPosts, CancellationToken ct)
    {
        var (limits, own) = await ContextAsync(workspaceId, ct);
        var others = own.Where(id => id != workspaceId).ToList();
        if (limits.Groups is { } maxGroups && await links.CountByWorkspacesAsync(others, ct) + linkCount > maxGroups)
            throw new DomainException($"ไฟล์สำรองมีกลุ่ม/เพจ {linkCount:N0} รายการ เกินที่แผนปัจจุบันเก็บได้ ({maxGroups:N0})");
        if (limits.LibraryPosts is { } maxPosts && await posts.CountByWorkspacesAsync(others, ct) + libraryPosts > maxPosts)
            throw new DomainException($"ไฟล์สำรองมีโพสต์ {libraryPosts:N0} โพสต์ เกินที่แผนปัจจุบันเก็บในคลังโพสต์ได้ ({maxPosts:N0})");
    }

    /// <summary>What the owner of the workspace is used so far, for the billing page.</summary>
    public async Task<(int Groups, int Images, int LibraryPosts)> UsageOfOwnerAsync(IReadOnlyList<Guid> ownWorkspaceIds, CancellationToken ct) =>
        (await links.CountByWorkspacesAsync(ownWorkspaceIds, ct), await media.CountByWorkspacesAsync(ownWorkspaceIds, ct),
         await posts.CountByWorkspacesAsync(ownWorkspaceIds, ct));

    private async Task<(Domain.ValueObjects.EffectiveLimits Limits, List<Guid> Own)> ContextAsync(Guid workspaceId, CancellationToken ct)
    {
        var ws = await workspaces.GetByIdAsync(workspaceId, ct) ?? throw new NotFoundException("เวิร์กสเปซ", workspaceId);
        var owner = await users.OwnerOfAsync(ws, ct);
        var limits = await plans.ForAsync(owner, ct);
        var own = (await workspaces.ListByOwnerAsync(owner.Id, ct)).Select(w => w.Id).ToList();
        return (limits, own);
    }
}
