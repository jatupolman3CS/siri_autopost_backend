using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Domain.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default);
    Task<User?> GetByStripeCustomerAsync(string customerId, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    void Add(User user);
}

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListAllAsync(CancellationToken ct = default);
    /// <summary>The user's role as a member (not as owner); null when they are not one.</summary>
    Task<WorkspaceRole?> GetMemberRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    void Add(Workspace workspace);
}

public interface IMemberRepository
{
    Task<IReadOnlyList<WorkspaceMember>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListByWorkspacesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListPendingAsync(string normalizedEmail, CancellationToken ct = default);
    Task<WorkspaceMember?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<WorkspaceMember?> GetForUserAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    void Add(WorkspaceMember member);
    void Remove(WorkspaceMember member);
}

public interface IPlanRepository
{
    Task<IReadOnlyList<PlanSetting>> ListAsync(CancellationToken ct = default);
    Task<PlanSetting> GetAsync(PlanKey key, CancellationToken ct = default);
}

public interface ITransactionRepository
{
    Task<IReadOnlyList<Transaction>> ListByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Transaction>> ListAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<Transaction?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Transaction?> GetByInvoiceAsync(string stripeInvoiceId, CancellationToken ct = default);
    /// <summary>The charge a Stripe payment intent paid for.</summary>
    Task<Transaction?> GetChargeByPaymentIntentAsync(string paymentIntentId, CancellationToken ct = default);
    Task<Transaction?> GetByRefundAsync(string stripeRefundId, CancellationToken ct = default);
    void Add(Transaction transaction);
}

public interface IPaymentEventRepository
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    void Add(ProcessedPaymentEvent processed);
}

public interface IAuditRepository
{
    /// <summary>Newest first; customerId null = every entry.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(Guid? customerId, int take, CancellationToken ct = default);
    /// <summary>Every plan change since a time (the plan history behind churn and MRR).</summary>
    Task<IReadOnlyList<AuditEntry>> ListPlanChangesAsync(DateTimeOffset since, CancellationToken ct = default);
    void Add(AuditEntry entry);
}

public interface IPromoRepository
{
    Task<IReadOnlyList<Promo>> ListAsync(CancellationToken ct = default);
    Task<Promo?> GetByCodeAsync(string code, CancellationToken ct = default);
    void Add(Promo promo);
}

public interface IAccountRepository
{
    Task<IReadOnlyList<SocialAccount>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Accounts connected through a paired browser, in any of these workspaces.</summary>
    Task<int> CountConnectedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    Task<IReadOnlyList<SocialAccount>> ListConnectedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    Task<SocialAccount?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<SocialAccount?> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(SocialAccount account);
}

public interface IPostRepository
{
    Task<Post?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListOpenErrorsAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Post>> ListByStatusAsync(Guid workspaceId, PostStatus status, CancellationToken ct = default);
    /// <summary>Posts of an account that are still to go out: queued, or held while the extension was offline.</summary>
    Task<IReadOnlyList<Post>> ListOpenByAccountAsync(Guid accountId, CancellationToken ct = default);
    /// <summary>Posts sent in the last 7 days, per workspace.</summary>
    Task<Dictionary<Guid, int>> CountSentSinceAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>Queued posts of an account that are due at <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<Post>> ListDueAsync(Guid accountId, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Posts a device has taken and not reported on yet.</summary>
    Task<IReadOnlyList<Post>> ListClaimedByAsync(Guid deviceId, CancellationToken ct = default);
    /// <summary>Published posts of a platform in a workspace (connected accounts only, see below).</summary>
    Task<int> CountPublishedSinceAsync(Guid workspaceId, Platform platform, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>When the account last published something (the anti-ban gap is per account).</summary>
    Task<DateTimeOffset?> LastPublishedAtAsync(Guid accountId, CancellationToken ct = default);
    // The queries below count only posts of accounts connected through a paired browser: the sample
    // accounts' history of a new workspace never went out, so it counts toward no limit or report.

    /// <summary>Posts published in these workspaces since a time (the plan's posts per 24 hours).</summary>
    Task<int> CountPublishedSinceAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>Counts per status of posts scheduled in [from, to), per workspace.</summary>
    Task<IReadOnlyList<(Guid WorkspaceId, PostStatus Status, int Count)>> CountByStatusAsync(
        IEnumerable<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    /// <summary>The newest posts of these workspaces scheduled before a time, any status.</summary>
    Task<IReadOnlyList<Post>> ListRecentAsync(IEnumerable<Guid> workspaceIds, DateTimeOffset before, int take, CancellationToken ct = default);
    /// <summary>Failed posts not dismissed yet.</summary>
    Task<IReadOnlyList<Post>> ListFailedAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    void Add(Post post);
    void Remove(Post post);
    void RemoveRange(IEnumerable<Post> posts);

    // ---- schedules: what a schedule generated ----

    /// <summary>Queued posts of a schedule that are still in the future (tracked: the caller removes them).</summary>
    Task<IReadOnlyList<Post>> ListFutureQueuedByScheduleAsync(Guid scheduleId, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>
    /// The (TargetKey, SlotKey) of every post a schedule generated that was due at or after <paramref name="scheduledFrom"/>
    /// (whatever its status): a new run skips these, so generating twice never doubles posts.
    /// </summary>
    Task<IReadOnlyList<(string TargetKey, string SlotKey)>> ListScheduleKeysAsync(Guid scheduleId, DateTimeOffset scheduledFrom, CancellationToken ct = default);
    /// <summary>Posts of each schedule due in [from, to) (any status).</summary>
    Task<Dictionary<Guid, int>> CountByScheduleAsync(IEnumerable<Guid> scheduleIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    /// <summary>The earliest queued post still to go out, per schedule (schedules without one are left out).</summary>
    Task<Dictionary<Guid, DateTimeOffset>> NextQueuedAtByScheduleAsync(IEnumerable<Guid> scheduleIds, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Posts of one round of a schedule (same SlotKey, every target).</summary>
    Task<IReadOnlyList<Post>> ListBySlotAsync(Guid scheduleId, string slotKey, CancellationToken ct = default);
    /// <summary>Posts of a round that are not finished: queued, waiting or being posted. 0 = the round is over.</summary>
    Task<int> CountOpenInSlotAsync(Guid scheduleId, string slotKey, CancellationToken ct = default);

    // ---- links: per-link caps, cooldown and "do not repeat" ----

    /// <summary>Posts published to one link since a time (posts of connected accounts, tests included).</summary>
    Task<int> CountPublishedToLinkSinceAsync(Guid linkId, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>The same for several links at once (links without a post are left out).</summary>
    Task<Dictionary<Guid, int>> CountPublishedToLinksSinceAsync(IEnumerable<Guid> linkIds, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>When the link was last published to; null when never.</summary>
    Task<DateTimeOffset?> LastPublishedToLinkAtAsync(Guid linkId, CancellationToken ct = default);
    /// <summary>
    /// The collection posts most recently used for each link, newest first, at most <paramref name="take"/> per link:
    /// posts that went out or are still queued (failed and skipped ones were never seen by the group).
    /// </summary>
    Task<Dictionary<Guid, IReadOnlyList<Guid>>> ListRecentCollectionPostIdsByLinkAsync(IEnumerable<Guid> linkIds, int take, CancellationToken ct = default);

    // ---- limits and health of the engine (connected accounts only, tests excluded from the failure rate) ----

    /// <summary>Posts published in a workspace since a time, all platforms.</summary>
    Task<int> CountPublishedInWorkspaceSinceAsync(Guid workspaceId, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>Finished posts since a time (success, awaiting approval, failed) and how many of them failed.</summary>
    Task<(int Finished, int Failed)> CountOutcomesSinceAsync(Guid workspaceId, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>The newest finished outcomes of an account (success, awaiting approval, failed), newest first.</summary>
    Task<IReadOnlyList<PostStatus>> ListRecentOutcomesAsync(Guid accountId, int take, CancellationToken ct = default);

    // ---- reports (real posts only: accounts with a device, no test posts) ----

    /// <summary>Finished posts (success, awaiting approval, failed) scheduled in [from, to), without their text.</summary>
    Task<IReadOnlyList<PostOutcome>> ListOutcomesAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    /// <summary>Successful posts made from each collection post (tests excluded): the "posted" count.</summary>
    Task<Dictionary<Guid, int>> CountPostedByCollectionPostAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Posts published since a time per hour of the day (0-23) in a local calendar: the best-times suggestion.</summary>
    Task<IReadOnlyDictionary<int, int>> CountPublishedByHourAsync(Guid workspaceId, DateTimeOffset since, int utcOffsetMinutes, CancellationToken ct = default);
}

/// <summary>A finished post without its text: what the reports count.</summary>
public sealed record PostOutcome(
    Guid Id, Guid AccountId, Platform Platform, Guid? LinkId, string Target, string? TargetUrl, Guid? CollectionPostId,
    PostStatus Status, DateTimeOffset ScheduledAt, DateTimeOffset? PublishedAt);

/// <summary>A library entry without its bytes.</summary>
public sealed record MediaSummary(Guid Id, string Name, string ContentType, MediaKind Kind, long Size, int UsedCount, DateTimeOffset CreatedAt);

public interface IMediaRepository
{
    Task<IReadOnlyList<MediaSummary>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<MediaFile?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<int> CountExistingAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default);
    /// <summary>Counts one more use (a post scheduled with it) for each file.</summary>
    Task RecordUseAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default);
    void Add(MediaFile file);
}

public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<Device?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Device?> GetByKeyHashAsync(string keyHash, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListByWorkspacesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct = default);
    void Add(Device device);
    void Remove(Device device);
}

public interface IDeviceEventRepository
{
    /// <summary>Events of a workspace after <paramref name="afterSeq"/>, oldest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<DeviceEvent>> ListAfterAsync(Guid workspaceId, long afterSeq, int take, CancellationToken ct = default);
    /// <summary>The newest Seq of a workspace (0 when it has no events).</summary>
    Task<long> HeadAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Deletes all but the newest <paramref name="keep"/> events of a device.</summary>
    Task PruneAsync(Guid deviceId, int keep, CancellationToken ct = default);
    void Add(DeviceEvent e);
}

public interface IDevicePairingRepository
{
    Task<DevicePairing?> GetByCodeAsync(string code, CancellationToken ct = default);
    /// <summary>Deletes codes that expired before <paramref name="before"/> (used or not): nothing else cleans them up.</summary>
    Task DeleteExpiredAsync(DateTimeOffset before, CancellationToken ct = default);
    void Add(DevicePairing pairing);
}

public interface ISnippetRepository
{
    Task<IReadOnlyList<Snippet>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(Snippet snippet);
}

/// <summary>The extension's settings, media, run state, log and web commands of paired devices.</summary>
public interface IExtensionRepository
{
    Task<ExtensionConfig?> GetConfigAsync(Guid deviceId, CancellationToken ct = default);
    /// <summary>Revision and HasContent of a device's settings without loading the JSON (null: none saved).</summary>
    Task<(int Revision, bool HasContent)?> GetConfigHeadAsync(Guid deviceId, CancellationToken ct = default);
    /// <summary>Saved settings JSON of every device of a workspace (to know which images are in use).</summary>
    Task<IReadOnlyList<string>> ListSettingsAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(ExtensionConfig config);

    /// <summary>The ids among <paramref name="imageIds"/> the workspace already has.</summary>
    Task<IReadOnlySet<string>> ExistingImageIdsAsync(Guid workspaceId, IEnumerable<string> imageIds, CancellationToken ct = default);
    Task<ExtensionImage?> GetImageAsync(Guid workspaceId, string imageId, CancellationToken ct = default);
    /// <summary>Deletes images created before <paramref name="before"/> that are not in <paramref name="keep"/>.</summary>
    Task<int> DeleteImagesExceptAsync(Guid workspaceId, IReadOnlySet<string> keep, DateTimeOffset before, CancellationToken ct = default);
    void Add(ExtensionImage image);

    Task<DeviceState?> GetStateAsync(Guid deviceId, CancellationToken ct = default);
    void Add(DeviceState state);

    /// <summary>The newest log lines of a device, oldest first.</summary>
    Task<IReadOnlyList<DeviceLog>> ListLogsAsync(Guid deviceId, int take, CancellationToken ct = default);
    /// <summary>Timestamp of the newest stored line (0 when none).</summary>
    Task<long> LastLogTAsync(Guid deviceId, CancellationToken ct = default);
    /// <summary>Deletes all but the newest <paramref name="keep"/> lines.</summary>
    Task PruneLogsAsync(Guid deviceId, int keep, CancellationToken ct = default);
    Task ClearLogsAsync(Guid deviceId, CancellationToken ct = default);
    void AddRange(IEnumerable<DeviceLog> logs);

    Task<DeviceCommand?> GetCommandAsync(Guid deviceId, Guid commandId, CancellationToken ct = default);
    /// <summary>Commands not handed to the device yet, oldest first.</summary>
    /// <summary>Commands without a result yet (pending or sent), oldest first.</summary>
    Task<IReadOnlyList<DeviceCommand>> ListOpenCommandsAsync(Guid deviceId, CancellationToken ct = default);
    void Add(DeviceCommand command);
}

public interface ICollectionRepository
{
    /// <summary>In the order the web app shows them (SortOrder, then age).</summary>
    Task<IReadOnlyList<PostCollection>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<PostCollection?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>The highest SortOrder in the workspace; -1 when there is no collection.</summary>
    Task<int> MaxSortOrderAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(PostCollection collection);
    /// <summary>The collection's posts go with it (the database cascades).</summary>
    void Remove(PostCollection collection);
    void RemoveRange(IEnumerable<PostCollection> collections);
}

public interface ICollectionPostRepository
{
    /// <summary>Every post of the workspace, oldest first.</summary>
    Task<IReadOnlyList<CollectionPost>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Posts of one collection, oldest first.</summary>
    Task<IReadOnlyList<CollectionPost>> ListByCollectionAsync(Guid workspaceId, Guid collectionId, CancellationToken ct = default);
    Task<CollectionPost?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionPost>> ListByIdsAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(CollectionPost post);
    void AddRange(IEnumerable<CollectionPost> posts);
    void Remove(CollectionPost post);
    void RemoveRange(IEnumerable<CollectionPost> posts);
}

public interface ILinkSetRepository
{
    Task<IReadOnlyList<LinkSet>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    Task<LinkSet?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<LinkSet?> GetByNameAsync(Guid workspaceId, string name, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    Task<int> MaxSortOrderAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(LinkSet set);
    /// <summary>The set's links go with it (the database cascades).</summary>
    void Remove(LinkSet set);
    void RemoveRange(IEnumerable<LinkSet> sets);
}

public interface ISetLinkRepository
{
    /// <summary>Every link of the workspace, by set then SortOrder.</summary>
    Task<IReadOnlyList<SetLink>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>The links of one set in SortOrder.</summary>
    Task<IReadOnlyList<SetLink>> ListBySetAsync(Guid workspaceId, Guid linkSetId, CancellationToken ct = default);
    Task<SetLink?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SetLink>> ListByIdsAsync(Guid workspaceId, IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<int> CountBySetAsync(Guid linkSetId, CancellationToken ct = default);
    Task<int> MaxSortOrderAsync(Guid linkSetId, CancellationToken ct = default);
    void Add(SetLink link);
    void AddRange(IEnumerable<SetLink> links);
    void Remove(SetLink link);
    void RemoveRange(IEnumerable<SetLink> links);
}

public interface IScheduleRepository
{
    Task<IReadOnlyList<Schedule>> ListAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Active schedules of a workspace (the top-up works on these).</summary>
    Task<IReadOnlyList<Schedule>> ListActiveAsync(Guid workspaceId, CancellationToken ct = default);
    Task<Schedule?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
    Task<int> CountAsync(Guid workspaceId, CancellationToken ct = default);
    /// <summary>Schedules that use a collection (a collection in use cannot be deleted).</summary>
    Task<IReadOnlyList<Schedule>> ListByCollectionAsync(Guid workspaceId, Guid collectionId, CancellationToken ct = default);
    /// <summary>Schedules that use a link set (a link set in use cannot be deleted).</summary>
    Task<IReadOnlyList<Schedule>> ListByLinkSetAsync(Guid workspaceId, Guid linkSetId, CancellationToken ct = default);
    void Add(Schedule schedule);
    void Remove(Schedule schedule);
    void RemoveRange(IEnumerable<Schedule> schedules);
}

public interface IReportShareRepository
{
    Task<ReportShare?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<int> CountActiveAsync(Guid workspaceId, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Deletes links that expired before <paramref name="before"/>.</summary>
    Task DeleteExpiredAsync(DateTimeOffset before, CancellationToken ct = default);
    void Add(ReportShare share);
}
