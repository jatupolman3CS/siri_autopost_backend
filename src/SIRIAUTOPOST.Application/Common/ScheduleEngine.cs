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
        var seen = new HashSet<string>();
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
public sealed record MaterializeResult(int Created, DateTimeOffset? FirstAt, DateTimeOffset? LastAt, IReadOnlyList<Guid> MediaIds, int UsablePosts)
{
    public static readonly MaterializeResult None = new(0, null, null, [], 0);
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
    public async Task<MaterializeResult> MaterializeAsync(
        Workspace ws, Schedule s, DateOnly fromLocal, DateOnly toLocal, DateTimeOffset now, CancellationToken ct = default)
    {
        if (toLocal < fromLocal) return MaterializeResult.None;
        var collection = await collections.GetAsync(ws.Id, s.CollectionId, ct);
        var set = await linkSets.GetAsync(ws.Id, s.LinkSetId, ct);
        if (collection is null || set is null) return MaterializeResult.None;

        var usable = (await collectionPosts.ListByCollectionAsync(ws.Id, collection.Id, ct)).Where(p => p.IsUsable(collection)).ToList();
        if (usable.Count == 0) return MaterializeResult.None;
        var links = await setLinks.ListBySetAsync(ws.Id, set.Id, ct);
        var targets = ScheduleTargets.Resolve(set, links, await accounts.ListAsync(ws.Id, ct)).Where(t => t.Account is not null).ToList();
        if (targets.Count == 0) return new MaterializeResult(0, null, null, [], usable.Count);

        var existing = (await posts.ListScheduleKeysAsync(s.Id, s.ToUtc(fromLocal, "00:00"), ct)).ToHashSet();

        var anti = ws.AntiBan;
        var avoidLast = anti.Advanced.RecentAvoid;
        // Shuffle: what each target had lately (newest first), from the database and then from this run.
        var recent = new Dictionary<string, List<Guid>>();
        if (s.Order == PostOrder.Shuffle)
        {
            var history = await posts.ListRecentCollectionPostIdsByLinkAsync(
                targets.Where(t => t.Link is not null).Select(t => t.Link!.Id), Math.Max(1, avoidLast), ct);
            foreach (var t in targets)
                recent[t.TargetKey] = t.Link is { } l && history.TryGetValue(l.Id, out var h) ? h.ToList() : [];
        }
        var deck = new List<CollectionPost>();
        // With a lot of posts a link avoids the last N it had; with few it can only avoid the last one (or nothing, with one post).
        var avoidCount = usable.Count <= 1 ? 0 : avoidLast > 0 && usable.Count > avoidLast + 1 ? avoidLast : 1;

        CollectionPost Draw(string targetKey)
        {
            var history = recent[targetKey];
            var avoid = history.Take(avoidCount).ToHashSet();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var i = deck.FindIndex(p => !avoid.Contains(p.Id));
                if (i >= 0)
                {
                    var picked = deck[i];
                    deck.RemoveAt(i);
                    history.Insert(0, picked.Id);
                    return picked;
                }
                deck = Shuffled(usable); // the deck is empty, or only holds what this link just had: deal a new one
            }
            var last = usable[0]; // unreachable: at least one post is not in the avoid set
            history.Insert(0, last.Id);
            return last;
        }

        var cursor = s.Cursor;
        var created = new List<Post>();
        for (var day = fromLocal; day <= toLocal; day = day.AddDays(1))
        {
            if (!s.Matches(day)) continue;
            var buckets = new SortedDictionary<string, List<ScheduleTarget>>(StringComparer.Ordinal);
            foreach (var t in targets)
                foreach (var slot in s.SlotsFor(t.OverrideKey))
                {
                    if (!buckets.TryGetValue(slot, out var list)) buckets[slot] = list = [];
                    list.Add(t);
                }

            foreach (var (slot, members) in buckets)
            {
                var at = s.ToUtc(day, slot);
                var slotKey = Schedule.SlotKey(day, slot);
                var fresh = new List<(ScheduleTarget Target, DateTimeOffset At)>();
                for (var i = 0; i < members.Count; i++)
                {
                    // The ones of a slot follow each other: the first at the slot, the rest after the smart delay.
                    if (i > 0) at = at.AddMinutes(anti.Min + random.NextDouble() * (anti.Max - anti.Min));
                    if (at > now && !existing.Contains((members[i].TargetKey, slotKey))) fresh.Add((members[i], at));
                }
                if (fresh.Count == 0) continue;
                if (created.Count + fresh.Count > Schedule.MaxPostsPerRun)
                    throw new DomainException(
                        $"ตารางนี้จะสร้างโพสต์เกิน {Schedule.MaxPostsPerRun:N0} โพสต์ในครั้งเดียว ลดจำนวนกลุ่มหรือจำนวนเวลาโพสต์ แล้วลองใหม่");

                CollectionPost? shared = null;
                if (s.Order == PostOrder.Rotate)
                {
                    shared = usable[cursor % usable.Count];
                    cursor++;
                }
                foreach (var (t, when) in fresh)
                {
                    var cp = shared ?? Draw(t.TargetKey);
                    var code = t.Link?.Code;
                    var text = PostComposer.ComposeFull(cp.Text, code, collection.Settings, random.NextDouble);
                    var target = t.Link is { } l ? l.Name : t.Account!.DefaultTarget;
                    created.Add(Post.FromSchedule(
                        ws.Id, t.Account!, target, text, cp.MediaIds, when, now, s.Id, cp.Id, t.Link?.Id, t.TargetKey, slotKey, t.Link?.Url, code));
                }
            }
        }

        foreach (var post in created) posts.Add(post);
        s.MarkGenerated(s.GeneratedThrough is { } done && done > toLocal ? done : toLocal, cursor);
        return created.Count == 0
            ? new MaterializeResult(0, null, null, [], usable.Count)
            : new MaterializeResult(created.Count, created.Min(p => p.ScheduledAt), created.Max(p => p.ScheduledAt),
                created.SelectMany(p => p.MediaIds).Distinct().ToList(), usable.Count);
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
/// Keeps schedules ahead of the clock: the posts of every active schedule are made a fortnight ahead in the
/// schedule's own calendar. <see cref="EnsureAsync"/> is cheap when nothing is due (one query) and runs at the start of
/// every device claim; the schedule handlers use <see cref="GenerateAsync"/>. Each run is its own unit of work, so
/// callers save their own changes first.
/// </summary>
public sealed class ScheduleTopUp(
    IWorkspaceRepository workspaces, IScheduleRepository schedules, ScheduleMaterializer materializer, IMediaRepository media,
    IUnitOfWork uow, TimeProvider clock)
{
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
    /// Fills every active schedule of the workspace that is behind. A schedule that cannot be generated (text too long,
    /// too many posts) is left for the next call; a Once schedule whose day has passed is switched off.
    /// </summary>
    public async Task<int> EnsureAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = new List<Guid>();
        var expired = false;
        foreach (var s in await schedules.ListActiveAsync(workspaceId, ct))
        {
            if (s.Mode == ScheduleMode.Once && s.StartDate < s.LocalDay(now))
            {
                s.SetActive(false);
                expired = true;
            }
            else if (Window(s, now) is not null) due.Add(s.Id);
        }
        if (expired) await uow.SaveChangesAsync(ct);
        var created = 0;
        foreach (var id in due)
        {
            try
            {
                created += (await GenerateAsync(workspaceId, id, ct)).Created;
            }
            catch (DomainException)
            {
                // Not generated this time (nothing was added); the next call tries again.
            }
        }
        return created;
    }

    /// <summary>Generates what a schedule is missing and saves it.</summary>
    public async Task<MaterializeResult> GenerateAsync(Guid workspaceId, Guid scheduleId, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = clock.GetUtcNow();
            var ws = await workspaces.GetByIdAsync(workspaceId, ct);
            var s = await schedules.GetAsync(workspaceId, scheduleId, ct);
            if (ws is null || s is null || Window(s, now) is not { } window) return MaterializeResult.None;
            var result = await materializer.MaterializeAsync(ws, s, window.From, window.To, now, ct);
            try
            {
                await uow.SaveChangesAsync(ct);
            }
            catch (DuplicateKeyException)
            {
                // Another request generated the same slots a moment ago: forget this attempt and fill in what is still missing.
                uow.DiscardChanges();
                continue;
            }
            if (result.MediaIds.Count > 0) await media.RecordUseAsync(workspaceId, result.MediaIds, ct); // once per run, not per post
            return result;
        }
        return MaterializeResult.None;
    }
}
