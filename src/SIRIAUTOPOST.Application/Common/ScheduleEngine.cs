using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// Something a schedule posts to: a link of the link set (through the account whose browser posts the set) or one of
/// the set's other accounts (to its default target, with no group code).
/// </summary>
/// <param name="OverrideKey">The key of the schedule's own times for it: the link's id (32 hex digits) or "account:&lt;id&gt;".</param>
/// <param name="TargetKey">What a generated post stores to say who it is for ("link:&lt;id&gt;" or "account:&lt;id&gt;").</param>
/// <param name="Account">The account that posts it; null for a link nobody can post right now (no connected Facebook account).</param>
public sealed record ScheduleTarget(string OverrideKey, string TargetKey, SetLink? Link, SocialAccount? Account);

public static class ScheduleTargets
{
    /// <summary>
    /// The targets of a link set in their order: the links that are on and valid (the second link with an address the
    /// set already has is left out: a group never gets the same post twice), then the other accounts.
    /// </summary>
    public static IReadOnlyList<ScheduleTarget> Resolve(LinkSet set, IReadOnlyList<SetLink> links, IReadOnlyList<SocialAccount> accounts)
    {
        var poster = LinkSetAccounts.PostingAccount(set, accounts);
        var linkAccount = poster is { IsConnected: true, Platform: Platform.Fb } ? poster : null;
        var list = new List<ScheduleTarget>();
        var seen = new HashSet<string>(FacebookGroupUrl.Comparer);
        foreach (var link in links)
        {
            if (!link.IsUsable || !seen.Add(link.Url)) continue;
            list.Add(new ScheduleTarget(Schedule.LinkKey(link.Id), Post.LinkTargetKey(link.Id), link, linkAccount));
        }
        foreach (var id in set.AccountIds.Distinct())
            if (accounts.FirstOrDefault(a => a.Id == id) is { } account)
                list.Add(new ScheduleTarget(Schedule.AccountKey(account.Id), Post.AccountTargetKey(account.Id), null, account));
        return list;
    }
}

/// <param name="MediaIds">Library files the new posts use (distinct): one use each is recorded per run.</param>
/// <param name="UsablePosts">Collection posts the schedule may use (0 = nothing can be generated).</param>
/// <param name="Advanced">
/// The run got as far as marking days as generated (or there was nothing left to do). False when nothing could be made
/// (no usable post, no connected browser, a lost race): the top-up leaves such a schedule alone for a while.
/// </param>
public sealed record MaterializeResult(
    int Created, DateTimeOffset? FirstAt, DateTimeOffset? LastAt, IReadOnlyList<Guid> MediaIds, int UsablePosts, bool Advanced = false)
{
    public static readonly MaterializeResult None = new(0, null, null, [], 0);

    /// <summary>Nothing to do: the schedule (or workspace) is gone, or it is already up to date.</summary>
    public static readonly MaterializeResult UpToDate = None with { Advanced = true };
}

/// <summary>
/// Turns a schedule into ordinary queued posts for a range of its local days: which slots, which targets in a slot
/// (staggered by the anti-ban delay), which collection post for each (rotate or shuffle), the composed text. It adds
/// the posts and updates the schedule; the caller saves. Running it again for the same days adds only what is missing.
/// </summary>
public sealed class ScheduleMaterializer(
    IPostRepository posts, ICollectionRepository collections, ICollectionPostRepository collectionPosts, ILinkSetRepository linkSets,
    ISetLinkRepository setLinks, IAccountRepository accounts, IRandomSource random)
{
    /// <summary>The refusal of a schedule whose single day holds more posts than one run may make.</summary>
    public static readonly string TooManyPerDay =
        $"ตารางนี้มีงานเกิน {Schedule.MaxPostsPerRun:N0} งานในวันเดียว ลดจำนวนกลุ่มหรือจำนวนเวลาโพสต์ แล้วลองใหม่";

    public async Task<MaterializeResult> MaterializeAsync(
        Workspace ws, Schedule s, DateOnly fromLocal, DateOnly toLocal, DateTimeOffset now, CancellationToken ct = default)
    {
        if (toLocal < fromLocal) return MaterializeResult.None;
        var collection = await collections.GetAsync(ws.Id, s.CollectionId, ct);
        var set = await linkSets.GetAsync(ws.Id, s.LinkSetId, ct);
        if (collection is null || set is null || !collection.Active || !set.Active) return MaterializeResult.None;

        var usable = (await collectionPosts.ListByCollectionAsync(ws.Id, collection.Id, ct)).Where(p => p.IsUsable(collection)).ToList();
        if (usable.Count == 0) return MaterializeResult.None;
        var links = await setLinks.ListBySetAsync(ws.Id, set.Id, ct);
        var targets = ScheduleTargets.Resolve(set, links, await accounts.ListAsync(ws.Id, ct)).Where(t => t.Account is not null).ToList();
        if (targets.Count == 0) return new MaterializeResult(0, null, null, [], usable.Count);

        var existing = (await posts.ListScheduleKeysAsync(s.Id, s.ToUtc(fromLocal, "00:00"), ct)).ToHashSet();

        var anti = ws.AntiBan;
        var avoidLast = anti.Advanced.RecentAvoid;
        var repeat = s.Repeat;
        // Shuffle: what each target had lately (newest first) and, for "never", every post it ever had; from the database
        // first, then from this run.
        var recent = new Dictionary<string, List<Guid>>();
        var everHad = new Dictionary<string, HashSet<Guid>>();
        var usableIds = usable.Select(p => p.Id).ToHashSet();
        if (s.Order == PostOrder.Shuffle)
        {
            var linkIds = targets.Where(t => t.Link is not null).Select(t => t.Link!.Id).ToList();
            // "Any" asks the database for nothing: a group may get any post again.
            var history = repeat == PostRepeat.Any
                ? new Dictionary<Guid, IReadOnlyList<Guid>>()
                : await posts.ListRecentCollectionPostIdsByLinkAsync(linkIds, Math.Max(1, avoidLast), ct);
            var all = repeat == PostRepeat.Never
                ? await posts.ListAllCollectionPostIdsByLinkAsync(linkIds, ct)
                : new Dictionary<Guid, IReadOnlyList<Guid>>();
            foreach (var t in targets)
            {
                recent[t.TargetKey] = t.Link is { } l && history.TryGetValue(l.Id, out var h) ? h.ToList() : [];
                everHad[t.TargetKey] = t.Link is { } l2 && all.TryGetValue(l2.Id, out var ever) ? ever.ToHashSet() : [];
            }
        }
        var deck = new List<CollectionPost>();
        // With a lot of posts a link avoids the last N it had; with few it can only avoid the last one (or nothing, with one post).
        var avoidCount = repeat == PostRepeat.Any || usable.Count <= 1 ? 0 : avoidLast > 0 && usable.Count > avoidLast + 1 ? avoidLast : 1;

        // The posts a target must not get now.
        HashSet<Guid> Avoid(string targetKey)
        {
            if (repeat == PostRepeat.Any) return [];
            var history = recent[targetKey];
            if (repeat == PostRepeat.Never)
            {
                var had = everHad[targetKey];
                if (usableIds.Count(had.Contains) < usable.Count) return had; // a post it has not had is left: nothing is given twice
                // It has had them all: it starts over, keeping away from only the last ones it had.
                had.Clear();
                had.UnionWith(history.Take(avoidCount));
                return had;
            }
            return history.Take(avoidCount).ToHashSet();
        }

        // A post's own limits: its days, hours, dates, and how many times a local day it may go out (over every group).
        var perDay = new Dictionary<(Guid Post, DateOnly Day), int>();
        var limited = usable.Where(p => p.Settings.MaxPerDay > 0).Select(p => p.Id).ToList();
        if (limited.Count > 0)
        {
            var offset = TimeSpan.FromMinutes(s.UtcOffsetMinutes);
            foreach (var (postId, at) in await posts.ListScheduledAtByCollectionPostAsync(
                         limited, s.ToUtc(fromLocal, "00:00"), s.ToUtc(toLocal.AddDays(1), "00:00"), ct))
            {
                var key = (postId, DateOnly.FromDateTime(at.ToOffset(offset).DateTime));
                perDay[key] = perDay.GetValueOrDefault(key) + 1;
            }
        }

        bool Allowed(CollectionPost p, DateOnly day, int minutes, int uses = 1) =>
            p.Settings.AllowsAt(day, minutes) && (p.Settings.MaxPerDay == 0 || perDay.GetValueOrDefault((p.Id, day)) + uses <= p.Settings.MaxPerDay);

        // The post a target gets now, among the posts that allow this slot. A post the group had lately is left alone when
        // another one can go; when only that one can, it goes (the schedule is not left empty because of a rule).
        CollectionPost? Draw(string targetKey, Func<CollectionPost, bool> allowed)
        {
            var history = recent[targetKey];
            var avoid = Avoid(targetKey);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var i = deck.FindIndex(p => !avoid.Contains(p.Id) && allowed(p));
                if (i >= 0)
                {
                    var picked = deck[i];
                    deck.RemoveAt(i);
                    history.Insert(0, picked.Id);
                    everHad[targetKey].Add(picked.Id);
                    return picked;
                }
                deck = Shuffled(usable); // the deck is empty, or only holds what this link just had or what this slot does not allow: deal a new one
            }
            var last = usable.FirstOrDefault(allowed);
            if (last is null) return null;
            history.Insert(0, last.Id);
            everHad[targetKey].Add(last.Id);
            return last;
        }

        var cursor = s.Cursor;
        var created = new List<Post>();

        // One round of a slot: its collection post (one for all in Rotate, one per target otherwise), the composed text, the post.
        // A slot nobody's post allows (a post limited to evenings, to a weekday, to a date range) makes nothing.
        void Queue(string slotKey, DateOnly day, int minutes, List<(ScheduleTarget Target, DateTimeOffset At)> fresh)
        {
            if (created.Count + fresh.Count > Schedule.MaxPostsPerRun) throw new DomainException(TooManyPerDay);

            CollectionPost? shared = null;
            if (s.Order == PostOrder.Rotate)
            {
                // The next post in turn that allows the slot; the cursor moves past the ones that did not.
                for (var step = 0; step < usable.Count && shared is null; step++)
                {
                    var candidate = usable[(cursor + step) % usable.Count];
                    if (!Allowed(candidate, day, minutes, fresh.Count)) continue;
                    shared = candidate;
                    cursor += step + 1;
                }
                if (shared is null) return;
            }
            foreach (var (t, when) in fresh)
            {
                var cp = shared ?? Draw(t.TargetKey, p => Allowed(p, day, minutes));
                if (cp is null) continue;
                if (cp.Settings.MaxPerDay > 0) perDay[(cp.Id, day)] = perDay.GetValueOrDefault((cp.Id, day)) + 1;
                var code = t.Link?.Code;
                var text = PostComposer.ComposeFull(cp.Text, code, cp.SettingsIn(collection), random.NextDouble);
                var target = t.Link is { } l ? l.Name : t.Account!.DefaultTarget;
                created.Add(Post.FromSchedule(
                    ws.Id, t.Account!, target, text, cp.MediaIds, when, now, s.Id, cp.Id, t.Link?.Id, t.TargetKey, slotKey, t.Link?.Url, code));
            }
        }

        // The groups of a round follow each other like a person moving from group to group: in a random order (when
        // the anti-ban "shuffle the order of target groups" is on), the first at the start of the round and each next one
        // after the smart delay (a random time between Min and Max minutes). Whole seconds: the database keeps
        // microseconds and a time should read back as it was made.
        List<ScheduleTarget> Ordered(List<ScheduleTarget> members) => anti.Shuffle ? ShuffledTargets(members) : members;

        List<(ScheduleTarget Target, DateTimeOffset At)> Stagger(
            IReadOnlyList<ScheduleTarget> members, DateTimeOffset first, string slotKey, bool firstIsNow)
        {
            var at = first;
            var fresh = new List<(ScheduleTarget Target, DateTimeOffset At)>();
            for (var i = 0; i < members.Count; i++)
            {
                if (i > 0) at = at.AddSeconds(Math.Round((anti.Min + random.NextDouble() * (anti.Max - anti.Min)) * 60));
                // A start-now round begins a few seconds after the click, not on the very second.
                else if (firstIsNow) at = at.AddSeconds(Math.Round(2 + random.NextDouble() * 18));
                if (at > now && !existing.Contains((members[i].TargetKey, slotKey))) fresh.Add((members[i], at));
            }
            return fresh;
        }

        // "Start now": every target once, beginning at this moment, before the regular times.
        if (s.NowPending)
        {
            var slotKey = Schedule.SlotKey(s.LocalDay(now), Schedule.NowSlot);
            var fresh = Stagger(Ordered(targets), now, slotKey, firstIsNow: true);
            var localNow = now.ToOffset(TimeSpan.FromMinutes(s.UtcOffsetMinutes));
            if (fresh.Count > 0) Queue(slotKey, s.LocalDay(now), localNow.Hour * 60 + localNow.Minute, fresh);
            s.ClearNowPending();
        }

        // A Once schedule that starts now has no time of its own: the round above is all of it.
        var regular = !(s.Mode == ScheduleMode.Once && s.StartNow);
        // A run makes whole days, at most MaxPostsPerRun posts: a big schedule (many groups, several times a day) gets
        // the days that fit now and the rest from the next top-up, because GeneratedThrough stops at the last day made.
        // Only one day that is bigger than that on its own is refused.
        var through = toLocal;
        for (var day = fromLocal; regular && day <= toLocal; day = day.AddDays(1))
        {
            if (!s.Matches(day)) continue;
            var buckets = new SortedDictionary<string, List<ScheduleTarget>>(StringComparer.Ordinal);
            foreach (var t in targets)
                foreach (var slot in s.SlotsFor(t.OverrideKey))
                {
                    if (!buckets.TryGetValue(slot, out var list)) buckets[slot] = list = [];
                    list.Add(t);
                }

            // The most the day can add (today's past slots counted too, so it is never less than what is made).
            var most = buckets.Sum(b => b.Value.Count(t => !existing.Contains((t.TargetKey, Schedule.SlotKey(day, b.Key)))));
            if (created.Count + most > Schedule.MaxPostsPerRun)
            {
                if (created.Count == 0) throw new DomainException(TooManyPerDay);
                through = day.AddDays(-1);
                break;
            }

            foreach (var (slot, members) in buckets)
            {
                var slotKey = Schedule.SlotKey(day, slot);
                var fresh = Stagger(Ordered(members), s.ToUtc(day, slot), slotKey, firstIsNow: false);
                if (fresh.Count > 0) Queue(slotKey, day, TimeOfDay.Parse(slot) ?? 0, fresh);
            }
        }

        if (created.Count > 0)
        {
            // The workspace's queue has a limit too: a run that would go beyond it adds nothing.
            var queued = await posts.CountQueuedFutureAsync(ws.Id, now, ct);
            if (queued + created.Count > Schedule.MaxQueuedPerWorkspace)
                throw new QueueFullException(queued, created.Count, Schedule.MaxQueuedPerWorkspace);
        }
        foreach (var post in created) posts.Add(post);
        s.MarkGenerated(s.GeneratedThrough is { } done && done > through ? done : through, cursor);
        return created.Count == 0
            ? new MaterializeResult(0, null, null, [], usable.Count, Advanced: true)
            : new MaterializeResult(created.Count, created.Min(p => p.ScheduledAt), created.Max(p => p.ScheduledAt),
                created.SelectMany(p => p.MediaIds).Distinct().ToList(), usable.Count, Advanced: true);
    }

    private List<ScheduleTarget> ShuffledTargets(List<ScheduleTarget> source)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Math.Min(i, (int)(random.NextDouble() * (i + 1)));
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // Fisher-Yates with the injected randomness (deterministic in tests).
    private List<CollectionPost> Shuffled(IReadOnlyList<CollectionPost> source)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Math.Min(i, (int)(random.NextDouble() * (i + 1)));
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}

/// <summary>
/// Schedules the top-up could not move forward are left alone for <see cref="Delay"/>, so a schedule that cannot be made
/// (no usable post, text too long, queue full, errors) does not rerun the whole materializer on every claim. One per
/// process; creating, resuming and restoring a schedule do not consult it.
/// </summary>
public sealed class TopUpThrottle
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(10);
    private const int PruneAbove = 1000;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> until = new();

    public bool IsDelayed(Guid scheduleId, DateTimeOffset now)
    {
        if (!until.TryGetValue(scheduleId, out var at)) return false;
        if (now < at) return true;
        until.TryRemove(new KeyValuePair<Guid, DateTimeOffset>(scheduleId, at));
        return false;
    }

    public void Hold(Guid scheduleId, DateTimeOffset now)
    {
        if (until.Count > PruneAbove)
            foreach (var (id, at) in until)
                if (at <= now) until.TryRemove(new KeyValuePair<Guid, DateTimeOffset>(id, at));
        until[scheduleId] = now + Delay;
    }

    public void Clear(Guid scheduleId) => until.TryRemove(scheduleId, out _);
}

/// <summary>
/// Keeps schedules ahead of the clock: the posts of every active schedule are made a fortnight ahead in the
/// schedule's own calendar. <see cref="EnsureAsync"/> is cheap when nothing is due (one query) and runs at the start of
/// every device claim, where it never throws; the schedule handlers use <see cref="GenerateAsync"/>. Each run is its own
/// unit of work, so callers save their own changes first. Runs for one schedule go one after another (a database lock
/// per schedule), so claims that arrive together fill a gap once instead of deadlocking on each other's inserts.
/// </summary>
public sealed class ScheduleTopUp(
    IWorkspaceRepository workspaces, IScheduleRepository schedules, ScheduleMaterializer materializer, IMediaRepository media,
    IUnitOfWork uow, TimeProvider clock, TopUpThrottle throttle, ILogger<ScheduleTopUp> log, INotificationDispatcher? notifier = null)
{
    /// <summary>A run that loses a race (a duplicate slot, a deadlock) starts again from a fresh read this many times.</summary>
    public const int Attempts = 3;

    /// <summary>The local days still to generate for a schedule, or null when it is up to date (or not due yet).</summary>
    public static (DateOnly From, DateOnly To)? Window(Schedule s, DateTimeOffset now)
    {
        var today = s.LocalDay(now);
        if (s.Mode == ScheduleMode.Once)
            return s.GeneratedThrough is null && s.StartDate >= today ? (s.StartDate, s.StartDate) : null;
        var from = today;
        if (s.StartDate > from) from = s.StartDate;
        if (s.GeneratedThrough is { } done && done.AddDays(1) > from) from = done.AddDays(1);
        var to = today.AddDays(Schedule.HorizonDays - 1);
        return from <= to ? (from, to) : null;
    }

    /// <summary>
    /// Fills every active schedule of the workspace that is behind. It never throws (except when the request is
    /// cancelled): a schedule that cannot be generated (text too long, too many posts, the queue is full, a database
    /// error) is logged, left alone for ten minutes and does not stop the others or the claim it runs in. A Once
    /// schedule whose day has passed is switched off.
    /// </summary>
    public async Task<int> EnsureAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var created = 0;
        try
        {
            var now = clock.GetUtcNow();
            var due = new List<Guid>();
            var expired = new List<Schedule>();
            foreach (var s in await schedules.ListActiveAsync(workspaceId, ct))
            {
                try
                {
                    if (s.Mode == ScheduleMode.Once && s.StartDate < s.LocalDay(now))
                    {
                        s.SetActive(false);
                        expired.Add(s);
                    }
                    else if (Window(s, now) is not null && !throttle.IsDelayed(s.Id, now)) due.Add(s.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Hold(s.Id, now, ex);
                }
            }
            if (expired.Count > 0)
            {
                await uow.SaveChangesAsync(ct);
                // A once-only schedule has run its day: say so (a courtesy, never a reason to fail the claim).
                if (notifier is not null)
                    await EngineNotices.SendAsync(notifier, workspaceId, expired.Select(EngineNotices.ScheduleFinished), ct);
            }
            foreach (var id in due) created += await EnsureOneAsync(workspaceId, id, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            uow.DiscardChanges(); // leave the context clean for the claim that follows
            log.LogWarning(ex, "Schedule top-up of workspace {WorkspaceId} failed", workspaceId);
        }
        return created;
    }

    private async Task<int> EnsureOneAsync(Guid workspaceId, Guid scheduleId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var result = await GenerateAsync(workspaceId, scheduleId, ct);
            if (!result.Advanced)
            {
                throttle.Hold(scheduleId, now);
                log.LogInformation("Schedule {ScheduleId} could not be topped up (no usable post, no connected browser or a lost race); trying again in {Minutes} minutes",
                    scheduleId, TopUpThrottle.Delay.TotalMinutes);
            }
            return result.Created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            uow.DiscardChanges();
            Hold(scheduleId, now, ex);
            return 0;
        }
    }

    private void Hold(Guid scheduleId, DateTimeOffset now, Exception ex)
    {
        throttle.Hold(scheduleId, now);
        log.LogWarning(ex, "Schedule {ScheduleId} could not be topped up; trying again in {Minutes} minutes", scheduleId, TopUpThrottle.Delay.TotalMinutes);
    }

    /// <summary>
    /// Generates what a schedule is missing and saves it, in a transaction that holds the schedule's lock: another
    /// request generating the same schedule waits and then finds the gap already filled. A run that still loses a race
    /// (a duplicate slot, a deadlock) starts again from a fresh read, at most <see cref="Attempts"/> times, then gives up
    /// quietly with a result that did not advance. A refusal (<see cref="DomainException"/>) is thrown to the caller.
    /// </summary>
    public async Task<MaterializeResult> GenerateAsync(Guid workspaceId, Guid scheduleId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = MaterializeResult.None;
            try
            {
                await uow.ExecuteInTransactionAsync($"schedule:{scheduleId:N}", async () =>
                {
                    uow.DiscardChanges(); // what was read before the lock may be out of date
                    var now = clock.GetUtcNow();
                    var ws = await workspaces.GetByIdAsync(workspaceId, ct);
                    var s = await schedules.GetAsync(workspaceId, scheduleId, ct);
                    if (ws is null || s is null || Window(s, now) is not { } window)
                    {
                        result = MaterializeResult.UpToDate;
                        return;
                    }
                    result = await materializer.MaterializeAsync(ws, s, window.From, window.To, now, ct);
                    await uow.SaveChangesAsync(ct);
                }, ct);
            }
            catch (Exception ex) when (ex is DuplicateKeyException or ConcurrencyConflictException && !uow.InTransaction)
            {
                // Another request generated the same slots a moment ago (or the database picked this run as the loser
                // of a deadlock): forget this attempt and fill in what is still missing.
                uow.DiscardChanges();
                if (attempt < Attempts) continue;
                log.LogWarning(ex, "Schedule {ScheduleId} lost the race to generate its posts {Attempts} times", scheduleId, Attempts);
                return MaterializeResult.None;
            }
            if (result.MediaIds.Count > 0) await media.RecordUseAsync(workspaceId, result.MediaIds, ct); // once per run, not per post
            if (result.Advanced) throttle.Clear(scheduleId);
            return result;
        }
    }
}
