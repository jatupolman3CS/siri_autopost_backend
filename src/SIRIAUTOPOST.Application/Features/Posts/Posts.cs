using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Extension;
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
            // A browser's Facebook account posts to groups it has synced: with none, the post could only fail when claimed.
            if (account.IsConnected && !account.PostsToGroups)
                throw new DomainException($"บัญชี {account.Name} ยังไม่มีกลุ่ม: เปิดส่วนขยายแล้วเพิ่มกลุ่มในชุดโพสต์ก่อน");
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
        if (mediaIds.Count > 0) await media.RecordUseAsync(ws.Id, mediaIds, ct); // once per scheduling, not per post
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

/// <summary>Puts up to 500 failed posts back in the queue at once (the error page's selection bar).</summary>
public sealed record RetryPostsCommand(Guid WorkspaceId, IReadOnlyList<Guid> PostIds) : ICommand<RetryPostsResultDto>;

/// <param name="Retried">Posts put back in the queue.</param>
/// <param name="Unbound">Failed posts left as they are: the browser they were for has been unbound, so none would ever take them.</param>
/// <param name="NotFailed">Ids that are not failed posts of this workspace any more (already retried, skipped, waiting for approval or gone).</param>
public sealed record RetryPostsResultDto(int Retried, int Unbound, int NotFailed);

public sealed class RetryPostsCommandHandler(
    IWorkspaceRepository workspaces, IAccountRepository accounts, IPostRepository posts, ICurrentUser current, IUnitOfWork uow,
    TimeProvider clock)
    : ICommandHandler<RetryPostsCommand, RetryPostsResultDto>
{
    public const int MaxItems = 500;

    public async Task<RetryPostsResultDto> HandleAsync(RetryPostsCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var ids = c.PostIds.Distinct().ToList();
        var failed = (await posts.ListByIdsAsync(c.WorkspaceId, ids, ct)).Where(p => p.Status == PostStatus.Failed).ToList();
        var connected = (await accounts.ListAsync(c.WorkspaceId, ct)).Where(a => a.IsConnected).Select(a => a.Id).ToHashSet();
        var now = clock.GetUtcNow();
        var retried = 0;
        foreach (var post in failed.Where(p => connected.Contains(p.AccountId)))
        {
            post.Retry(now);
            retried++;
        }
        if (retried > 0) await uow.SaveChangesAsync(ct);
        return new RetryPostsResultDto(retried, failed.Count - retried, ids.Count - failed.Count);
    }
}

/// <summary>
/// "Post now" (the timeline, the schedules' dots, the calendar) and the manual rerun of a failed post: the post leaves
/// its slot, is due this moment and is handed out before every other due post of its browser; the browser is told to
/// take it now instead of at its next 30-second round. Only a browser that is paired can post it (the claim only hands
/// a device the posts of its own account), and a customer the platform admin stopped cannot post at all.
/// </summary>
public sealed record RunPostNowCommand(Guid WorkspaceId, Guid PostId) : ICommand<PostDto>;

public sealed class RunPostNowCommandHandler(
    IWorkspaceRepository workspaces, IAccountRepository accounts, IPostRepository posts, IExtensionRepository ext,
    IDeviceEventRepository events, IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<RunPostNowCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(RunPostNowCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var post = await posts.GetAsync(c.WorkspaceId, c.PostId, ct) ?? throw new NotFoundException("โพสต์", c.PostId);
        var account = await accounts.GetAsync(ws.Id, post.AccountId, ct);
        if (account?.DeviceId is not { } deviceId)
            throw new DomainException("เบราว์เซอร์ของบัญชีนี้ถูกยกเลิกการผูกแล้ว จึงไม่มีเครื่องที่จะโพสต์ ผูกเครื่องใหม่ที่หน้าทีมก่อน");
        if (!(await users.OwnerOfAsync(ws, ct)).CanPost)
            throw new DomainException("บัญชีเจ้าของเวิร์กสเปซถูกระงับหรือหยุดการโพสต์โดยผู้ดูแลแพลตฟอร์ม จึงสั่งให้เครื่องโพสต์ไม่ได้");

        var now = clock.GetUtcNow();
        post.RunNow(now);
        var wake = DeviceCommand.Create(ws.Id, deviceId, "takeJobs", "{}", now);
        ext.Add(wake);
        events.Add(CommandEvents.Of(wake, now)); // wakes a device waiting in a long sync
        events.Add(DeviceEvents.PostChanged(ws.Id, deviceId, post, now));
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
