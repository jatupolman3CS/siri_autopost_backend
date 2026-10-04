using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>Which account's browser posts a link set, and the checks on the accounts a set names.</summary>
public static class LinkSetAccounts
{
    /// <summary>
    /// The account that posts the set's links: the one it names (<see cref="LinkSet.PostAsAccountId"/>), or the
    /// workspace's first connected Facebook account. The named account is returned even when it is not connected, so
    /// the caller can say so. Null when there is none.
    /// </summary>
    public static SocialAccount? PostingAccount(LinkSet set, IReadOnlyList<SocialAccount> accounts)
    {
        if (set.PostAsAccountId is { } id && accounts.FirstOrDefault(a => a.Id == id) is { } named) return named;
        return accounts.FirstOrDefault(a => a.Platform == Platform.Fb && a.IsConnected);
    }

    /// <summary>The device a "links.changed" event of this set belongs to; null when no browser posts the set.</summary>
    public static async Task<Guid?> EventDeviceAsync(LinkSet set, IAccountRepository accounts, CancellationToken ct) =>
        PostingAccount(set, await accounts.ListAsync(set.WorkspaceId, ct))?.DeviceId;

    /// <summary>The account must belong to the workspace; for "post as" it must be a Facebook account.</summary>
    public static void EnsureValid(Guid? postAs, IEnumerable<Guid> others, IReadOnlyList<SocialAccount> accounts)
    {
        if (postAs is { } id)
        {
            var account = accounts.FirstOrDefault(a => a.Id == id) ?? throw new DomainException("ไม่พบบัญชีที่ใช้โพสต์ลิงก์ในเวิร์กสเปซนี้");
            if (account.Platform != Platform.Fb) throw new DomainException("บัญชีที่ใช้โพสต์ลิงก์กลุ่มต้องเป็นบัญชี Facebook");
        }
        var known = accounts.Select(a => a.Id).ToHashSet();
        if (others.Any(o => !known.Contains(o))) throw new DomainException("ไม่พบบัญชีบางรายการในเวิร์กสเปซนี้");
    }
}
