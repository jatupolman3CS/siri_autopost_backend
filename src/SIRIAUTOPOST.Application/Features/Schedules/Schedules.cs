using System.Globalization;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Application.Features.Schedules;

// Schedules ("ตารางโพสต์"): a collection + a link set + when to post. They turn into queued posts (ScheduleMaterializer),
// which the paired browser claims. Viewers read, editors write.

/// <summary>Builds the schedules' DTOs: the numbers the web app shows next to each schedule.</summary>
public sealed class ScheduleViews(
    ICollectionRepository collections, ICollectionPostRepository collectionPosts, ILinkSetRepository linkSets,
    ISetLinkRepository setLinks, IAccountRepository accounts, IPostRepository posts)
{
    public async Task<IReadOnlyList<ScheduleDto>> BuildAsync(
        Guid workspaceId, IReadOnlyList<Schedule> list, DateTimeOffset now, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var byCollection = (await collections.ListAsync(workspaceId, ct)).ToDictionary(c => c.Id);
        var usable = (await collectionPosts.ListAsync(workspaceId, ct))
            .Where(p => byCollection.TryGetValue(p.CollectionId, out var c) && p.IsUsable(c))
            .GroupBy(p => p.CollectionId)
            .ToDictionary(g => g.Key, g => g.Count());
        var sets = (await linkSets.ListAsync(workspaceId, ct)).ToDictionary(s => s.Id);
        var links = (await setLinks.ListAsync(workspaceId, ct)).ToLookup(l => l.LinkSetId);
        var accountList = await accounts.ListAsync(workspaceId, ct);

        var ids = list.Select(s => s.Id).ToList();
        var next = await posts.NextQueuedAtByScheduleAsync(ids, now, ct);
        // "Today" is the schedule's own local day: schedules in the same time zone share one query.
        var today = new Dictionary<Guid, int>();
        foreach (var group in list.GroupBy(s => s.ToUtc(s.LocalDay(now), "00:00")))
            foreach (var (id, count) in await posts.CountByScheduleAsync(group.Select(s => s.Id), group.Key, group.Key.AddDays(1), ct))
                today[id] = count;

        return list.Select(s =>
        {
            var targets = sets.TryGetValue(s.LinkSetId, out var set) ? ScheduleTargets.Resolve(set, links[set.Id].ToList(), accountList) : [];
            return Map(s, targets.Count, targets.Sum(t => s.SlotsFor(t.OverrideKey).Count), usable.GetValueOrDefault(s.CollectionId),
                today.GetValueOrDefault(s.Id), next.TryGetValue(s.Id, out var at) ? at : null);
        }).ToList();
    }

    public async Task<ScheduleDto> BuildOneAsync(Schedule s, DateTimeOffset now, CancellationToken ct) =>
        (await BuildAsync(s.WorkspaceId, [s], now, ct))[0];

    public static ScheduleDto Map(Schedule s, int targetCount, int perDay, int usablePosts, int todayCount, DateTimeOffset? nextRunAt) =>
        new(s.Id, s.Name, s.CollectionId, s.LinkSetId, s.Mode, s.Times, s.EveryHours, s.FirstTime,
            s.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.OnceTime, s.Order, s.DripFrom, s.DripTo, s.DripCount,
            s.BumpHours, s.AutoDeleteDays,
            s.Overrides.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value),
            s.Active, s.UtcOffsetMinutes, s.Slots(), targetCount, perDay, usablePosts, todayCount, nextRunAt, s.StartNow);
}

public sealed record GetSchedulesQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<ScheduleDto>>;

public sealed class GetSchedulesQueryHandler(
    IWorkspaceRepository workspaces, IScheduleRepository schedules, ScheduleViews views, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetSchedulesQuery, IReadOnlyList<ScheduleDto>>
{
    public async Task<IReadOnlyList<ScheduleDto>> HandleAsync(GetSchedulesQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return await views.BuildAsync(q.WorkspaceId, await schedules.ListAsync(q.WorkspaceId, ct), clock.GetUtcNow(), ct);
    }
}

/// <summary>
/// Creates a schedule and queues its posts for the next fortnight (in the schedule's local calendar). Refused (422)
/// when the collection or link set is missing, the workspace already has 50 schedules, the collection has no post
/// that can be used, no connected Facebook account can post the set, or the posts would take the workspace over its
/// limit of queued posts. The schedule and its posts are saved together: a refusal leaves nothing behind.
/// </summary>
public sealed record CreateScheduleCommand(Guid WorkspaceId, SaveScheduleRequest Request) : ICommand<ScheduleCreatedDto>;

public sealed class CreateScheduleCommandHandler(
    IWorkspaceRepository workspaces, IScheduleRepository schedules, ICollectionRepository collections, ICollectionPostRepository collectionPosts,
    ILinkSetRepository linkSets, ISetLinkRepository setLinks, IAccountRepository accounts, ScheduleTopUp topUp, ScheduleViews views,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateScheduleCommand, ScheduleCreatedDto>
{
    public const string NoDevice = "ยังไม่มีบัญชี Facebook ที่ผูกกับเครื่อง ผูกเครื่องในหน้าทีมและเวิร์กสเปซก่อนสร้างตารางโพสต์";

    public async Task<ScheduleCreatedDto> HandleAsync(CreateScheduleCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var r = c.Request;
        var collection = await collections.GetAsync(ws.Id, r.CollectionId, ct) ?? throw new DomainException("ไม่พบชุดโพสต์ที่เลือก");
        var set = await linkSets.GetAsync(ws.Id, r.LinkSetId, ct) ?? throw new DomainException("ไม่พบชุดลิงก์ที่เลือก");

        var now = clock.GetUtcNow();
        if (r.UtcOffsetMinutes is < -840 or > 840) throw new DomainException("เขตเวลาไม่ถูกต้อง");
        var today = Schedule.LocalDayOf(now, r.UtcOffsetMinutes);
        var start = r.StartNow || string.IsNullOrWhiteSpace(r.StartDate)
            ? today
            : DateOnly.TryParseExact(r.StartDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new DomainException("วันที่เริ่มไม่ถูกต้อง ใช้รูปแบบ ปปปป-ดด-วว");

        if (!(await collectionPosts.ListByCollectionAsync(ws.Id, collection.Id, ct)).Any(p => p.IsUsable(collection)))
            throw new DomainException(collection.Settings.RequireApproval
                ? "ชุดโพสต์นี้ยังไม่มีโพสต์ที่อนุมัติแล้ว อนุมัติโพสต์ก่อนสร้างตาราง"
                : "ชุดโพสต์นี้ยังไม่มีโพสต์ เพิ่มโพสต์ก่อนสร้างตาราง");
        var accountList = await accounts.ListAsync(ws.Id, ct);
        var poster = LinkSetAccounts.PostingAccount(set, accountList);
        if (poster is null || poster.Platform != Platform.Fb) throw new DomainException(NoDevice);
        if (!poster.IsConnected) throw new DomainException($"บัญชี {poster.Name} ยังไม่ได้เชื่อมกับเครื่อง ผูกเครื่องในหน้าทีมและเวิร์กสเปซก่อนสร้างตารางโพสต์");
        var targets = ScheduleTargets.Resolve(set, await setLinks.ListBySetAsync(ws.Id, set.Id, ct), accountList);
        if (targets.Count == 0) throw new DomainException("ชุดลิงก์นี้ยังไม่มีกลุ่มที่เปิดใช้งานและลิงก์ถูกต้อง หรือบัญชีอื่นให้โพสต์");

        // Times for a target that is not in the set mean nothing: leave them out.
        var known = targets.Select(t => t.OverrideKey).ToHashSet();
        var overrides = (r.Overrides ?? new Dictionary<string, IReadOnlyList<string>>())
            .Select(kv => (Key: Schedule.NormalizeKey(kv.Key), kv.Value))
            .Where(o => known.Contains(o.Key))
            .GroupBy(o => o.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        // Without a name the schedule is called after what it pairs (cut to fit: the person did not type it).
        var name = r.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{collection.Name} → {set.Name}";
            if (name.Length > Schedule.MaxNameLength) name = name[..Schedule.MaxNameLength];
        }
        var schedule = Schedule.Create(
            ws.Id, name, collection.Id, set.Id, r.Mode, r.Times, r.EveryHours, r.FirstTime, start, r.OnceTime, r.Order, r.DripFrom, r.DripTo,
            r.DripCount, r.BumpHours, r.AutoDeleteDays, overrides, r.UtcOffsetMinutes, now, r.StartNow);
        var made = MaterializeResult.None;
        // One creation of a workspace at a time (the limit of schedules is counted under the lock); the generation joins this transaction.
        await uow.ExecuteInTransactionAsync($"schedules:{ws.Id:N}", async () =>
        {
            if (await schedules.CountAsync(ws.Id, ct) >= Schedule.MaxPerWorkspace)
                throw new DomainException($"สร้างตารางโพสต์ได้ไม่เกิน {Schedule.MaxPerWorkspace} ตารางต่อเวิร์กสเปซ");
            schedules.Add(schedule);
            await uow.SaveChangesAsync(ct);
            made = await topUp.GenerateAsync(ws.Id, schedule.Id, ct);
        }, ct);
        var saved = await schedules.GetAsync(ws.Id, schedule.Id, ct) ?? schedule;
        return new ScheduleCreatedDto(await views.BuildOneAsync(saved, now, ct), made.Created, made.FirstAt, made.LastAt);
    }
}

/// <summary>
/// Pauses or resumes a schedule. Pausing drops all its posts that are still waiting for a browser (queued, due or not,
/// and held ones; never one being posted or finished); resuming makes the next
/// fortnight again (and is refused, leaving the schedule paused, when the posts would not fit in the workspace's queue).
/// </summary>
public sealed record SetScheduleActiveCommand(Guid WorkspaceId, Guid ScheduleId, bool Active) : ICommand<ScheduleDto>;

public sealed class SetScheduleActiveCommandHandler(
    IWorkspaceRepository workspaces, IScheduleRepository schedules, IPostRepository posts, ScheduleTopUp topUp, ScheduleViews views,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SetScheduleActiveCommand, ScheduleDto>
{
    public async Task<ScheduleDto> HandleAsync(SetScheduleActiveCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var schedule = await schedules.GetAsync(ws.Id, c.ScheduleId, ct) ?? throw new NotFoundException("ตารางโพสต์", c.ScheduleId);
        var now = clock.GetUtcNow();
        if (schedule.Active != c.Active)
        {
            if (!c.Active)
            {
                posts.RemoveRange(await posts.ListOpenByScheduleAsync(schedule.Id, ct));
                schedule.SetActive(false);
                await uow.SaveChangesAsync(ct);
            }
            else
            {
                await uow.ExecuteInTransactionAsync($"schedules:{ws.Id:N}", async () =>
                {
                    schedule.SetActive(true);
                    await uow.SaveChangesAsync(ct);
                    await topUp.GenerateAsync(ws.Id, schedule.Id, ct);
                }, ct);
            }
        }
        var saved = await schedules.GetAsync(ws.Id, schedule.Id, ct) ?? schedule;
        return await views.BuildOneAsync(saved, now, ct);
    }
}

/// <summary>
/// Deletes a schedule and all its posts that are still waiting for a browser (queued, due or not, and held ones). What is
/// being posted or already went out stays in the history.
/// </summary>
public sealed record DeleteScheduleCommand(Guid WorkspaceId, Guid ScheduleId) : ICommand<Unit>;

public sealed class DeleteScheduleCommandHandler(
    IWorkspaceRepository workspaces, IScheduleRepository schedules, IPostRepository posts, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<DeleteScheduleCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteScheduleCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var schedule = await schedules.GetAsync(ws.Id, c.ScheduleId, ct) ?? throw new NotFoundException("ตารางโพสต์", c.ScheduleId);
        posts.RemoveRange(await posts.ListOpenByScheduleAsync(schedule.Id, ct));
        schedules.Remove(schedule);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>The three hours of the day (in the caller's time zone) with the most published posts in the last 30 days, as "HH:00" in order.</summary>
public sealed record GetBestTimesQuery(Guid WorkspaceId, int UtcOffsetMinutes) : IQuery<IReadOnlyList<string>>;

public sealed class GetBestTimesQueryHandler(
    IWorkspaceRepository workspaces, IPostRepository posts, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetBestTimesQuery, IReadOnlyList<string>>
{
    public const int Days = 30;
    public const int Top = 3;

    public async Task<IReadOnlyList<string>> HandleAsync(GetBestTimesQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        if (q.UtcOffsetMinutes is < -840 or > 840) throw new DomainException("เขตเวลาไม่ถูกต้อง");
        var byHour = await posts.CountPublishedByHourAsync(q.WorkspaceId, clock.GetUtcNow().AddDays(-Days), q.UtcOffsetMinutes, ct);
        return byHour.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(Top)
            .Select(kv => kv.Key)
            .Order()
            .Select(h => $"{h:00}:00")
            .ToList();
    }
}

/// <summary>
/// The test page: one real post, due now, composed like a schedule's task. The target is a link of the set (or the
/// first usable one), or another account of the workspace; the post is a given or a random usable one of the collection;
/// <paramref name="Text"/> replaces its text.
/// </summary>
public sealed record CreateTestPostCommand(
    Guid WorkspaceId, Guid LinkSetId, Guid? LinkId, Guid? AccountId, Guid CollectionId, Guid? CollectionPostId, string? Text) : ICommand<PostDto>;

public sealed class CreateTestPostCommandHandler(
    IWorkspaceRepository workspaces, ILinkSetRepository linkSets, ISetLinkRepository setLinks, IAccountRepository accounts,
    ICollectionRepository collections, ICollectionPostRepository collectionPosts, IPostRepository postRepo, IDeviceEventRepository events,
    IRandomSource random, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreateTestPostCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(CreateTestPostCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var collection = await collections.GetAsync(ws.Id, c.CollectionId, ct) ?? throw new NotFoundException("ชุดโพสต์", c.CollectionId);
        var set = await linkSets.GetAsync(ws.Id, c.LinkSetId, ct) ?? throw new NotFoundException("ชุดลิงก์", c.LinkSetId);
        var accountList = await accounts.ListAsync(ws.Id, ct);

        SocialAccount account;
        SetLink? link = null;
        if (c.LinkId is null && c.AccountId is { } accountId)
        {
            account = accountList.FirstOrDefault(a => a.Id == accountId) ?? throw new NotFoundException("บัญชี", accountId);
        }
        else
        {
            var links = await setLinks.ListBySetAsync(ws.Id, set.Id, ct);
            if (c.LinkId is { } linkId)
            {
                link = links.FirstOrDefault(l => l.Id == linkId) ?? throw new NotFoundException("ลิงก์กลุ่ม", linkId);
                if (!link.IsValid) throw new DomainException("ลิงก์นี้ไม่ใช่ลิงก์กลุ่ม Facebook ที่ถูกต้อง");
                if (!link.Enabled) throw new DomainException("กลุ่มนี้ถูกปิดอยู่ เปิดใช้งานกลุ่มก่อนทดสอบ");
            }
            else
            {
                link = ScheduleTargets.Resolve(set, links, accountList).FirstOrDefault(t => t.Link is not null)?.Link;
                if (link is null) throw new DomainException("ชุดลิงก์นี้ยังไม่มีกลุ่มที่ใช้ทดสอบได้ เลือกกลุ่มหรือบัญชีที่จะทดสอบ");
            }
            account = LinkSetAccounts.PostingAccount(set, accountList) ?? throw new DomainException(CreateScheduleCommandHandler.NoDevice);
        }

        var all = await collectionPosts.ListByCollectionAsync(ws.Id, collection.Id, ct);
        CollectionPost? picked;
        if (c.CollectionPostId is { } postId)
        {
            picked = all.FirstOrDefault(p => p.Id == postId) ?? throw new NotFoundException("โพสต์ในชุดโพสต์", postId);
            if (!picked.IsUsable(collection)) throw new DomainException("โพสต์นี้ยังไม่ได้รับการอนุมัติ");
        }
        else
        {
            var usable = all.Where(p => p.IsUsable(collection)).ToList();
            picked = usable.Count == 0 ? null : usable[Math.Min(usable.Count - 1, (int)(random.NextDouble() * usable.Count))];
        }
        var text = string.IsNullOrWhiteSpace(c.Text) ? picked?.Text : c.Text.Trim();
        if (text is null) throw new DomainException("ชุดโพสต์นี้ยังไม่มีโพสต์ที่ใช้ได้ ใส่ข้อความที่ต้องการทดสอบ");

        var code = link?.Code;
        var composed = PostComposer.ComposeFull(text, code, collection.Settings, random.NextDouble);
        var now = clock.GetUtcNow();
        var post = Post.Test(
            ws.Id, account, link is null ? account.DefaultTarget : link.Name, composed, picked?.MediaIds ?? [], now, picked?.Id, link?.Id,
            link?.Url, code);
        postRepo.Add(post);
        if (account.DeviceId is { } deviceId) events.Add(DeviceEvents.PostChanged(ws.Id, deviceId, post, now));
        await uow.SaveChangesAsync(ct);
        return PostDto.From(post);
    }
}
