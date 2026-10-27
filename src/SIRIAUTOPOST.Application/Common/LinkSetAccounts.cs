using SIRIAUTOPOST.Application.Features.Schedules;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>Which account's browser posts a link set, and the checks on the accounts a set names.</summary>
public static class LinkSetAccounts
{
    /// <summary>
    /// The account that posts the set's links. A set that names one (<see cref="LinkSet.PostAsAccountId"/>) is posted by
    /// that browser and by no other: while its browser is unbound the named account is still the answer (so the caller
    /// can say it is not connected and the posts wait), never another extension's account. A browser that pairs again
    /// takes its old account back (see <c>PairDeviceCommandHandler</c>), so the set goes on through it. The one exception
    /// is a named account that no longer exists: the workspace's only connected account of the set's platform takes over, and with
    /// several there is no guess. A set that names none is posted by the workspace's ONLY connected account of that platform;
    /// with two or more extensions (2026-10-10) there is no "first one" any more: the set belongs to nobody and waits
    /// until a person picks the extension, because a guess sends the posts of one extension's targets to another.
    /// Null when there is none.
    /// </summary>
    public static SocialAccount? PostingAccount(LinkSet set, IReadOnlyList<SocialAccount> accounts)
    {
        var platform = set.Channel;
        var connected = accounts.Where(a => a.Platform == platform && a.IsConnected).ToList();
        if (set.PostAsAccountId is not { } id) return connected.Count == 1 ? connected[0] : null;
        return accounts.FirstOrDefault(a => a.Id == id) ?? (connected.Count == 1 ? connected[0] : null);
    }

    /// <summary>The refusal of a schedule for a set that names no extension while several are connected.</summary>
    public const string NoChoice =
        "ชุดลิงก์นี้ยังไม่ได้เลือกเครื่องที่จะใช้โพสต์ เลือกเครื่อง (ส่วนขยาย) ในการ์ดของชุดลิงก์ก่อนสร้างตารางโพสต์";

    /// <summary>The refusal of a set that names "other accounts": a set is posted by one extension only.</summary>
    public const string OtherAccountsRetired =
        "ชุดลิงก์ใช้ได้กับเครื่องเดียว จึงไม่รองรับการให้บัญชีอื่นโพสต์ข้อความเดียวกันผ่านเครื่องอื่นอีกแล้ว สร้างชุดลิงก์แยกสำหรับเครื่องนั้นแทน";

    public const string NoLineDevice = "ยังไม่มีบัญชี LINE ที่ผูกกับเครื่อง ผูกเครื่องในหน้าทีมและเวิร์กสเปซก่อนสร้างตารางโพสต์";

    /// <summary>Why a set has no poster: no connected browser at all, or several and no choice.</summary>
    public static string NoPosterReason(IReadOnlyList<SocialAccount> accounts, Platform platform = Platform.Fb) =>
        accounts.Count(a => a.Platform == platform && a.IsConnected) >= 2
            ? NoChoice
            : (platform == Platform.Line ? NoLineDevice : CreateScheduleCommandHandler.NoDevice);

    /// <summary>The device a "links.changed" event of this set belongs to; null when no browser posts the set.</summary>
    public static async Task<Guid?> EventDeviceAsync(LinkSet set, IAccountRepository accounts, CancellationToken ct) =>
        PostingAccount(set, await accounts.ListAsync(set.WorkspaceId, ct))?.DeviceId;

    /// <summary>The account must belong to the workspace; for "post as" it must match the channel platform.</summary>
    public static void EnsureValid(Guid? postAs, IEnumerable<Guid> others, IReadOnlyList<SocialAccount> accounts, Platform platform = Platform.Fb)
    {
        if (postAs is { } id)
        {
            var account = accounts.FirstOrDefault(a => a.Id == id) ?? throw new DomainException("ไม่พบบัญชีที่ใช้โพสต์ลิงก์ในเวิร์กสเปซนี้");
            if (account.Platform != platform)
                throw new DomainException(platform == Platform.Line
                    ? "บัญชีที่ใช้ส่งแชทต้องเป็นบัญชี LINE"
                    : "บัญชีที่ใช้โพสต์ลิงก์กลุ่มต้องเป็นบัญชี Facebook");
        }
        var known = accounts.Select(a => a.Id).ToHashSet();
        if (others.Any(o => !known.Contains(o))) throw new DomainException("ไม่พบบัญชีบางรายการในเวิร์กสเปซนี้");
    }
}
