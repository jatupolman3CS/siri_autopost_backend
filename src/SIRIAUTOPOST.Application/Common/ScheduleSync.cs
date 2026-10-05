using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// Keeps the posts a schedule has already queued in step with what they were made from. A schedule queues a fortnight
/// ahead, so a link that is deleted, switched off or given another address or code (or a set whose posting account
/// changed) would otherwise go on posting the old way for two weeks. The handlers call this before they save, so the
/// removal and the edit are one change. Posts a device has taken or that went out are never touched, and posts that
/// are due now are left to the claim, which already skips a group that is off or gone.
/// The days a schedule has queued are forgotten (<see cref="Schedule.InvalidateGenerated"/>) so the next top-up
/// fills in what is missing; the slots that are still there are kept, so nothing is queued twice.
/// </summary>
public sealed class ScheduleSync(IScheduleRepository schedules, IPostRepository posts)
{
    /// <summary>
    /// Links of a set are gone, off, or post to another address or with another code: their queued posts for the future
    /// are removed, and the schedules of the set make what is missing again (the post for the new address or code).
    /// </summary>
    public async Task LinksChangedAsync(Guid workspaceId, Guid linkSetId, IEnumerable<Guid> linkIds, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var id in linkIds.Distinct()) posts.RemoveRange(await posts.ListFutureQueuedByLinkAsync(id, now, ct));
        await InvalidateAsync(workspaceId, linkSetId, ct);
    }

    /// <summary>Something to post to appeared in the set (a link was added or switched on): its schedules look at their horizon again.</summary>
    public Task LinksAddedAsync(Guid workspaceId, Guid linkSetId, CancellationToken ct) => InvalidateAsync(workspaceId, linkSetId, ct);

    /// <summary>
    /// The set's posting account or its other accounts changed. When <paramref name="dropQueued"/> (the account that posts
    /// changed, or an account was taken out) every post its schedules still have queued for the future is removed, because
    /// they belong to the old setup; a set that only gained an account just has its schedules look again.
    /// </summary>
    public async Task SetChangedAsync(Guid workspaceId, Guid linkSetId, bool dropQueued, DateTimeOffset now, CancellationToken ct)
    {
        var all = await schedules.ListByLinkSetAsync(workspaceId, linkSetId, ct);
        foreach (var s in all)
        {
            if (dropQueued) posts.RemoveRange(await posts.ListFutureQueuedByScheduleAsync(s.Id, now, ct));
            if (s.Active) s.InvalidateGenerated();
        }
    }

    /// <summary>
    /// A collection or link set was switched off (the future posts its schedules still have queued go, because nothing may
    /// be posted from it) or on again (the schedules look at their horizon again and fill in what is missing).
    /// </summary>
    public async Task SourceToggledAsync(IReadOnlyList<Schedule> affected, bool active, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var s in affected)
        {
            if (!active) posts.RemoveRange(await posts.ListFutureQueuedByScheduleAsync(s.Id, now, ct));
            if (s.Active) s.InvalidateGenerated();
        }
    }

    private async Task InvalidateAsync(Guid workspaceId, Guid linkSetId, CancellationToken ct)
    {
        foreach (var s in await schedules.ListByLinkSetAsync(workspaceId, linkSetId, ct))
            if (s.Active) s.InvalidateGenerated();
    }
}

/// <summary>Lets the schedules of a collection or link set that was switched on again fill their horizon at once.</summary>
internal static class SourceTopUp
{
    public static async Task RunAsync(ScheduleTopUp topUp, Guid workspaceId, IEnumerable<Schedule> schedules, CancellationToken ct)
    {
        foreach (var s in schedules.Where(s => s.Active))
        {
            try
            {
                await topUp.GenerateAsync(workspaceId, s.Id, ct);
            }
            catch (DomainException)
            {
                // A refusal (queue full, text too long) must not undo the switch: the next claim's top-up tries again.
            }
        }
    }
}
