using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// A post belongs to the extension that owns its link set TODAY. After a set was handed to another extension (the
/// timeline's "change extension"), a post that failed or was skipped before the hand-over still names the old account;
/// putting it back in the queue (retry, post now) would leave it waiting for a browser that does not post that set any
/// more, so it follows the set to the new extension first.
/// </summary>
internal static class PostHoming
{
    /// <summary>
    /// The connected account that owns the set of the post's link, when it is not the account the post has now; null when
    /// the post is already with its owner, is a test post or not a schedule's, or the owner is not paired to a browser.
    /// </summary>
    public static async Task<SocialAccount?> NewOwnerAsync(
        Post post, ISetLinkRepository links, ILinkSetRepository sets, IReadOnlyList<SocialAccount> accounts, CancellationToken ct)
    {
        if (post.LinkId is not { } linkId || post.ScheduleId is null || post.IsTest) return null;
        var link = await links.GetAsync(post.WorkspaceId, linkId, ct);
        var set = link is null ? null : await sets.GetAsync(post.WorkspaceId, link.LinkSetId, ct);
        if (set is null || LinkOwnership.OwnerOf(set, accounts) is not { } ownerId || ownerId == post.AccountId) return null;
        return accounts.FirstOrDefault(a => a.Id == ownerId) is { IsConnected: true } owner ? owner : null;
    }
}
