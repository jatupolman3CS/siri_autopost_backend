namespace SIRIAUTOPOST.Domain.Enums;

// Serialized as snake_case strings (fb, pending_approval, ...), the same keys the web app uses.

/// <summary>
/// Where a post goes. Facebook (groups and pages) is the only platform for now; the other networks come in a later
/// phase. The value stays so the API keeps saying <c>fb</c> and a second platform can be added without a contract change.
/// </summary>
public enum Platform
{
    Fb,
}

public enum PostStatus
{
    Queued,
    Posting,
    Success,
    Failed,
    Skipped,
    /// <summary>Submitted to a group that needs admin approval. Not an error.</summary>
    Pending,
    /// <summary>Due while the extension was offline.</summary>
    Waiting,
}

public enum FailureCode
{
    RateLimit,
    Session,
    Network,
    PendingApproval,
    MediaTooLarge,
    Quota,
}

public enum AccountHealth
{
    Ok,
    Warn,
    Relogin,
}

public enum UserRole
{
    User,
    Admin,
}

public enum PlanKey
{
    Free,
    Basic,
    Pro,
    Agency,
}

public enum MediaKind
{
    Image,
    Video,
}

public enum OfflinePolicy
{
    Skip,
    Queue,
    Notify,
}

/// <summary>The customer's standing with the platform (shown to the platform admin).</summary>
public enum CustomerStatus
{
    Active,
    /// <summary>
    /// Legacy: a paid plan chosen at sign-up before payments went through Stripe. Nothing sets it any more
    /// (a new account starts on Free and buys a plan at Checkout); old rows are cleared by the admin.
    /// </summary>
    Trial,
    /// <summary>A renewal payment failed; Stripe is retrying. The plan stays until the subscription ends.</summary>
    PastDue,
    /// <summary>Stopped by the platform admin: cannot sign in, devices take no posts.</summary>
    Suspended,
    Banned,
}

public enum BillingCycle
{
    Month,
    Year,
}

public enum TransactionType
{
    Charge,
    Refund,
    Failed,
}

/// <summary>A member's role in someone else's workspace (the owner is not a member).</summary>
public enum WorkspaceRole
{
    Viewer,
    Editor,
    Admin,
    Owner,
}

/// <summary>What an <c>AuditEntry</c> records.</summary>
public enum AuditAction
{
    PlanChanged,
    StatusChanged,
    PauseChanged,
    LimitsChanged,
    NoteChanged,
    DeviceRevoked,
    FailedRetried,
    Refunded,
    PaymentRecorded,
    /// <summary>The admin asked Stripe to collect a failed invoice again.</summary>
    PaymentRetried,
    PlanSettingsChanged,
    PromoCreated,
    PromoToggled,
    Impersonated,
    /// <summary>The admin changed the test amount that replaces the plan price for the listed customers.</summary>
    PaymentOverrideChanged,
    /// <summary>A customer's Checkout or plan change was charged the test amount instead of the plan price.</summary>
    PaymentOverrideUsed,
}

/// <summary>Where a bump (a comment that brings an old post back to the top) is.</summary>
public enum BumpStatus
{
    /// <summary>Waiting for its time and for the browser.</summary>
    Queued,
    /// <summary>The browser took it and is commenting now.</summary>
    Posting,
    Done,
    Failed,
    /// <summary>Left out: too late, or the post or the browser is gone.</summary>
    Skipped,
}

/// <summary>Where a <c>DeviceCommand</c> from the web app is.</summary>
public enum CommandStatus
{
    /// <summary>Waiting for the device's next sync.</summary>
    Pending,
    /// <summary>Handed to the device, no result yet.</summary>
    Sent,
    Done,
    /// <summary>Nobody took it in time; it never runs.</summary>
    Expired,
}

/// <summary>Where a collection post is in the approval flow (only collections that require approval use it).</summary>
public enum PostApproval
{
    Draft,
    Pending,
    Approved,
}

/// <summary>How a group link of a link set is doing.</summary>
public enum LinkHealth
{
    Ok,
    /// <summary>The group holds posts for admin approval.</summary>
    Pending,
    /// <summary>Switched off, by hand or automatically after repeated failures.</summary>
    Off,
}

/// <summary>When a schedule posts. Weekend is Friday to Sunday, as the design labels it.</summary>
public enum ScheduleMode
{
    Daily,
    Weekdays,
    Weekend,
    /// <summary>Rounds every N hours from a first time.</summary>
    Interval,
    /// <summary>N posts spread between two times of day.</summary>
    Drip,
    Once,
}

public enum PostOrder
{
    Shuffle,
    Rotate,
}

/// <summary>
/// Whether a group may get a post it already had, when the schedule picks posts at random (<see cref="PostOrder.Shuffle"/>):
/// not the ones it had lately (default), any post at any time, or no post twice until it has had them all.
/// </summary>
public enum PostRepeat
{
    /// <summary>Not one of the group's last N posts (N = the advanced anti-ban "recent avoid"; with few posts, only the last one).</summary>
    Recent,
    /// <summary>Any post: the same one may come again for the same group, even within a day.</summary>
    Any,
    /// <summary>A group never gets a post it has had before until it has had every post of the collection; then it starts over.</summary>
    Never,
}

public enum FooterPosition
{
    End,
    Top,
}

public enum WatermarkPosition
{
    Br,
    Bl,
    Tr,
    C,
}

/// <summary>Where a notification goes. Default means "use the parent": the workspace's for a set, the set's for a group.</summary>
public enum NotifyChannel
{
    Default,
    Tg,
    Line,
    Both,
    Off,
}

/// <summary>What a notification is about.</summary>
public enum NotifyEvent
{
    Success,
    Fail,
    Shot,
    Round,
    StartStop,
    Block,
    Offline,
    Quota,
}
