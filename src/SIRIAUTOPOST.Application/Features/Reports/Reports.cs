using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Reports;

// Reports: how the groups and the posts did over the last 7 or 30 days, and (Agency) a frozen copy of one that a
// client can open with a link. Likes and comments are not collected, so the report has none.

/// <summary>
/// Builds the report. Only real posts count (accounts with a device; the demo accounts' history never went out) and
/// test posts do not. A post belongs to the period by the time it was due (<c>ScheduledAt</c>), the same time the
/// calendar shows, whatever its outcome: failed posts have no publish time. Dismissing a failed post turns it into a
/// skipped one, which the report does not count.
/// </summary>
public static class ReportBuilder
{
    public const int MaxPosts = 20;
    public static readonly int[] Periods = [7, 30];

    private readonly record struct GroupKey(Guid? LinkId, string Target, Platform? Platform);

    /// <param name="outcomes">Finished posts (success, awaiting approval, failed) due in the period.</param>
    /// <param name="links">The workspace's links by id.</param>
    /// <returns>The report without its posts section (see <see cref="ReportComposer"/>).</returns>
    public static ReportDto Build(
        int days, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<PostOutcome> outcomes, IReadOnlyDictionary<Guid, SetLink> links)
    {
        // A group is its link; posts that did not come from a link (or whose link is gone) are grouped by what they were called.
        var groups = outcomes
            .GroupBy(o => o.LinkId is { } id && links.ContainsKey(id) ? new GroupKey(id, "", null) : new GroupKey(null, o.Target, o.Platform))
            .Select(g =>
            {
                var posted = g.Count(o => o.Status == PostStatus.Success);
                var pending = g.Count(o => o.Status == PostStatus.Pending);
                var failed = g.Count(o => o.Status == PostStatus.Failed);
                var platform = g.First().Platform;
                if (g.Key.LinkId is { } linkId)
                {
                    var link = links[linkId];
                    return new ReportGroupDto(
                        link.Id, link.Name.Length > 0 ? link.Name : link.Url, link.Url, platform, posted, pending, failed, RateOf(posted, failed), link.Enabled,
                        link.Health);
                }
                var url = g.Where(o => !string.IsNullOrWhiteSpace(o.TargetUrl)).OrderByDescending(o => o.ScheduledAt).Select(o => o.TargetUrl).FirstOrDefault();
                return new ReportGroupDto(null, g.Key.Target, url, platform, posted, pending, failed, RateOf(posted, failed), true, LinkHealth.Ok);
            })
            .OrderByDescending(g => g.Posted)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.LinkId)
            .ToList();
        return new ReportDto(days, from, to, groups, []);
    }

    /// <summary>The share of posts that went out among those that finished: round(posted / (posted + failed) * 100); 100 when none did.</summary>
    public static int RateOf(int posted, int failed) =>
        posted + failed == 0 ? 100 : (int)Math.Round(posted * 100.0 / (posted + failed), MidpointRounding.AwayFromZero);

    /// <summary>Successful posts per collection post, most used first (ties by id, so the order never changes between calls).</summary>
    public static IReadOnlyList<(Guid CollectionPostId, int Used)> UsageOf(IEnumerable<PostOutcome> outcomes) =>
        outcomes.Where(o => o.Status == PostStatus.Success && o.CollectionPostId != null)
            .GroupBy(o => o.CollectionPostId!.Value)
            .Select(g => (CollectionPostId: g.Key, Used: g.Count()))
            .OrderByDescending(x => x.Used)
            .ThenBy(x => x.CollectionPostId)
            .ToList();
}

/// <summary>Reads what the report counts and builds it; the query and the share command use it.</summary>
public sealed class ReportComposer(IPostRepository posts, ISetLinkRepository links, ICollectionPostRepository collectionPosts, TimeProvider clock)
{
    private const int PostChunk = 40;

    public async Task<ReportDto> BuildAsync(Guid workspaceId, int days, CancellationToken ct)
    {
        var to = clock.GetUtcNow();
        var from = to.AddDays(-days);
        var outcomes = await posts.ListOutcomesAsync(workspaceId, from, to, ct);
        var byId = (await links.ListAsync(workspaceId, ct)).ToDictionary(l => l.Id);
        var report = ReportBuilder.Build(days, from, to, outcomes, byId);

        // The most used posts that still exist (a deleted collection post has no text to show).
        var usage = ReportBuilder.UsageOf(outcomes);
        var top = new List<ReportPostDto>();
        for (var i = 0; i < usage.Count && top.Count < ReportBuilder.MaxPosts; i += PostChunk)
        {
            var chunk = usage.Skip(i).Take(PostChunk).ToList();
            var found = (await collectionPosts.ListByIdsAsync(workspaceId, chunk.Select(x => x.CollectionPostId), ct)).ToDictionary(p => p.Id);
            top.AddRange(chunk.Where(x => found.ContainsKey(x.CollectionPostId))
                .Select(x => new ReportPostDto(x.CollectionPostId, found[x.CollectionPostId].Text, x.Used)));
        }
        return report with { Posts = top.Take(ReportBuilder.MaxPosts).ToList() };
    }
}

public sealed record GetReportQuery(Guid WorkspaceId, int Days) : IQuery<ReportDto>;

public sealed class GetReportQueryHandler(IWorkspaceRepository workspaces, ReportComposer reports, ICurrentUser current) : IQueryHandler<GetReportQuery, ReportDto>
{
    public async Task<ReportDto> HandleAsync(GetReportQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        if (!ReportBuilder.Periods.Contains(q.Days))
            throw new ValidationException([new ValidationFailure("days", "ดูรายงานได้ 7 หรือ 30 วัน")]);
        return await reports.BuildAsync(ws.Id, q.Days, ct);
    }
}

/// <summary>How a shared report is stored: the API's JSON conventions (camelCase, snake_case enums), so it reads back as it was.</summary>
public static class ReportSnapshot
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

public sealed record ShareReportCommand(Guid WorkspaceId, string Brand, string Period, bool Logo) : ICommand<ReportShareDto>;

/// <summary>
/// Freezes the report as of now behind a random link (Agency only). A link lives 30 days, a workspace keeps at most
/// <see cref="MaxActive"/> of them, and every new one clears the expired links first.
/// </summary>
public sealed class ShareReportCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IReportShareRepository shares, ReportComposer reports, ICurrentUser current,
    IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ShareReportCommand, ReportShareDto>
{
    public const int MaxActive = 20;

    public async Task<ReportShareDto> HandleAsync(ShareReportCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireClientReportsAsync(ws, ct);

        var now = clock.GetUtcNow();
        await shares.DeleteExpiredAsync(now, ct);
        if (await shares.CountActiveAsync(ws.Id, now, ct) >= MaxActive)
            throw new DomainException($"สร้างลิงก์รายงานที่ยังไม่หมดอายุได้ไม่เกิน {MaxActive} ลิงก์ ลบหรือรอให้ลิงก์เก่าหมดอายุก่อน");

        var brand = (c.Brand ?? "").Trim();
        var report = await reports.BuildAsync(ws.Id, c.Period == "month" ? 30 : 7, ct);
        var snapshot = new SharedReportDto(brand, ws.Name, c.Logo, c.Period, now, now + ReportShare.Lifetime, report);
        var share = ReportShare.Create(
            ws.Id, ReportShare.NewToken(), brand, c.Period, c.Logo, JsonSerializer.Serialize(snapshot, ReportSnapshot.Options), now);
        shares.Add(share);
        await uow.SaveChangesAsync(ct);
        return new ReportShareDto(share.Token, "/report/" + share.Token, share.ExpiresAt);
    }
}

public sealed record GetReportSharesQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<ReportShareSummaryDto>>;

/// <summary>The workspace's live links, newest first (admins; Agency, like making them). Their paths carry the tokens.</summary>
public sealed class GetReportSharesQueryHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IReportShareRepository shares, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetReportSharesQuery, IReadOnlyList<ReportShareSummaryDto>>
{
    public async Task<IReadOnlyList<ReportShareSummaryDto>> HandleAsync(GetReportSharesQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireClientReportsAsync(ws, ct);
        return (await shares.ListActiveAsync(ws.Id, clock.GetUtcNow(), ct))
            .Select(x => new ReportShareSummaryDto(x.Id, x.Brand, x.Period, x.ShowLogo, x.CreatedAt, x.ExpiresAt, "/report/" + x.Token))
            .ToList();
    }
}

public sealed record RevokeReportShareCommand(Guid WorkspaceId, Guid ShareId) : ICommand<Unit>;

/// <summary>
/// Takes a link back: it stops working at once and no longer counts toward the twenty. Admins; no plan check, so a workspace
/// whose owner has left Agency can still clean up what it handed out.
/// </summary>
public sealed class RevokeReportShareCommandHandler(
    IWorkspaceRepository workspaces, IReportShareRepository shares, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<RevokeReportShareCommand, Unit>
{
    public async Task<Unit> HandleAsync(RevokeReportShareCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        var share = await shares.GetAsync(ws.Id, c.ShareId, ct) ?? throw new NotFoundException("ลิงก์รายงาน", c.ShareId);
        shares.Remove(share);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record GetSharedReportQuery(string Token) : IQuery<SharedReportDto>;

/// <summary>
/// The report behind a link. No sign-in: the token is the key. Unknown and expired links look the same (404), and so does a
/// link whose workspace's owner is no longer on the Agency plan or is blocked: a downgrade takes the links with it.
/// </summary>
public sealed class GetSharedReportQueryHandler(
    IReportShareRepository shares, IWorkspaceRepository workspaces, IUserRepository users, TimeProvider clock)
    : IQueryHandler<GetSharedReportQuery, SharedReportDto>
{
    public async Task<SharedReportDto> HandleAsync(GetSharedReportQuery q, CancellationToken ct = default)
    {
        var token = q.Token ?? "";
        // A token has a fixed shape: anything else cannot exist, so the database is not asked.
        if (token.Length != ReportShare.TokenLength || !token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new NotFoundException("รายงาน", "ลิงก์นี้");
        var share = await shares.GetByTokenAsync(token, ct);
        if (share is null || share.IsExpired(clock.GetUtcNow())) throw new NotFoundException("รายงาน", "ลิงก์นี้");
        var ws = await workspaces.GetByIdAsync(share.WorkspaceId, ct) ?? throw new NotFoundException("รายงาน", "ลิงก์นี้");
        var owner = await users.OwnerOfAsync(ws, ct);
        if (!owner.HasClientReports || owner.IsBlocked) throw new NotFoundException("รายงาน", "ลิงก์นี้");
        return JsonSerializer.Deserialize<SharedReportDto>(share.SnapshotJson, ReportSnapshot.Options) ?? throw new NotFoundException("รายงาน", "ลิงก์นี้");
    }
}
