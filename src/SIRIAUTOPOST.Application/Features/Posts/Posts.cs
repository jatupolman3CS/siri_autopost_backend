using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Posts;

public sealed record GetPostsQuery(Guid WorkspaceId, DateTimeOffset From, DateTimeOffset To) : IQuery<IReadOnlyList<PostDto>>;

public sealed class GetPostsQueryHandler(IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current)
    : IQueryHandler<GetPostsQuery, IReadOnlyList<PostDto>>
{
    public const int MaxRangeDays = 120;

    public async Task<IReadOnlyList<PostDto>> HandleAsync(GetPostsQuery q, CancellationToken ct = default)
    {
        if (q.To <= q.From || (q.To - q.From).TotalDays > MaxRangeDays)
            throw new DomainException($"ช่วงวันที่ต้องไม่เกิน {MaxRangeDays} วัน");
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return (await posts.ListAsync(q.WorkspaceId, q.From, q.To, ct)).Select(PostDto.From).ToList();
    }
}

/// <summary>Failed posts and posts waiting for group approval that the user has not dismissed.</summary>
public sealed record GetErrorsQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<PostDto>>;

public sealed class GetErrorsQueryHandler(IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current)
    : IQueryHandler<GetErrorsQuery, IReadOnlyList<PostDto>>
{
    public async Task<IReadOnlyList<PostDto>> HandleAsync(GetErrorsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return (await posts.ListOpenErrorsAsync(q.WorkspaceId, ct)).Select(PostDto.From).ToList();
    }
}

/// <summary>An account to post with; accounts that post to groups name the groups.</summary>
public sealed record TargetSelection(Guid AccountId, IReadOnlyList<string>? Groups);

public static class Repeat
{
    public const string None = "none";
    public const string Daily = "daily";
    public const string Weekdays = "weekdays";
    public const string Weekly = "weekly";
    public static readonly string[] All = [None, Daily, Weekdays, Weekly];
    /// <summary>Repeating schedules are queued this many days ahead.</summary>
    public const int HorizonDays = 14;
}

/// <summary>
/// Composer "Schedule": one task per target (each selected group of a group account, otherwise the
/// account's default target), starting at StartAt and spaced by the workspace smart delay.
/// StartAt carries the user's UTC offset, so weekdays follow their local calendar.
/// </summary>
public sealed record SchedulePostsCommand(
    Guid WorkspaceId,
    string Content,
    IReadOnlyList<Guid>? MediaIds,
    DateTimeOffset StartAt,
    bool UseDelay,
    string Repeat,
    IReadOnlyList<TargetSelection> Targets) : ICommand<ScheduleResultDto>;

public sealed class SchedulePostsCommandHandler(
    IWorkspaceRepository workspaces, IAccountRepository accounts, IMediaRepository media, IPostRepository posts,
    ICurrentUser current, IRandomSource random, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SchedulePostsCommand, ScheduleResultDto>
{
    public async Task<ScheduleResultDto> HandleAsync(SchedulePostsCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var all = (await accounts.ListAsync(ws.Id, ct)).ToDictionary(a => a.Id);

        var tasks = new List<(SocialAccount Account, string Target)>();
        foreach (var sel in c.Targets.DistinctBy(t => t.AccountId))
        {
            if (!all.TryGetValue(sel.AccountId, out var account)) throw new NotFoundException("บัญชี", sel.AccountId);
            if (!account.PostsToGroups)
            {
                tasks.Add((account, account.DefaultTarget));
                continue;
            }
            var groups = (sel.Groups ?? []).Distinct().ToList();
            if (groups.Count == 0) throw new DomainException($"เลือกกลุ่มของ {account.Name} อย่างน้อย 1 กลุ่ม");
            var unknown = groups.FirstOrDefault(g => !account.Groups.Contains(g));
            if (unknown is not null) throw new DomainException($"ไม่พบกลุ่ม \"{unknown}\" ในบัญชี {account.Name}");
            tasks.AddRange(groups.Select(g => (account, g)));
        }

        var mediaIds = (c.MediaIds ?? []).Distinct().ToList();
        if (mediaIds.Count > 0 && await media.CountExistingAsync(ws.Id, mediaIds, ct) != mediaIds.Count)
            throw new DomainException("ไม่พบไฟล์สื่อบางรายการในคลัง");

        var now = clock.GetUtcNow();
        var created = new List<Post>();
        foreach (var day in Occurrences(c.StartAt, c.Repeat))
        {
            var at = day;
            for (var i = 0; i < tasks.Count; i++)
            {
                if (i > 0 && c.UseDelay)
                    at = at.AddMinutes(ws.AntiBan.Min + random.NextDouble() * (ws.AntiBan.Max - ws.AntiBan.Min));
                var post = Post.Schedule(ws.Id, tasks[i].Account, tasks[i].Target, c.Content, mediaIds, at, now);
                posts.Add(post);
                created.Add(post);
            }
        }
        if (created.Count == 0) throw new DomainException("ไม่มีวันที่ตรงกับรูปแบบการทำซ้ำในอีก 14 วัน");

        await uow.SaveChangesAsync(ct);
        return new ScheduleResultDto(created.Count, created.Min(p => p.ScheduledAt), created.Max(p => p.ScheduledAt));
    }

    /// <summary>Start times of every occurrence within the 14-day horizon.</summary>
    public static IEnumerable<DateTimeOffset> Occurrences(DateTimeOffset start, string repeat) => repeat switch
    {
        Repeat.Daily => Enumerable.Range(0, Repeat.HorizonDays).Select(d => start.AddDays(d)),
        Repeat.Weekdays => Enumerable.Range(0, Repeat.HorizonDays).Select(d => start.AddDays(d))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)),
        Repeat.Weekly => Enumerable.Range(0, Repeat.HorizonDays / 7).Select(w => start.AddDays(7 * w)),
        _ => [start],
    };
}

public sealed record DeletePostCommand(Guid WorkspaceId, Guid PostId) : ICommand<Unit>;

public sealed class DeletePostCommandHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeletePostCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeletePostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await posts.GetAsync(c.WorkspaceId, c.PostId, ct) ?? throw new NotFoundException("โพสต์", c.PostId);
        post.EnsureDeletable();
        posts.Remove(post);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record RetryPostCommand(Guid WorkspaceId, Guid PostId) : ICommand<PostDto>;

public sealed class RetryPostCommandHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RetryPostCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(RetryPostCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await posts.GetAsync(c.WorkspaceId, c.PostId, ct) ?? throw new NotFoundException("โพสต์", c.PostId);
        post.Retry(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        return PostDto.From(post);
    }
}

public sealed record DismissPostErrorCommand(Guid WorkspaceId, Guid PostId) : ICommand<PostDto>;

public sealed class DismissPostErrorCommandHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DismissPostErrorCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(DismissPostErrorCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await posts.GetAsync(c.WorkspaceId, c.PostId, ct) ?? throw new NotFoundException("โพสต์", c.PostId);
        post.DismissError(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        return PostDto.From(post);
    }
}
