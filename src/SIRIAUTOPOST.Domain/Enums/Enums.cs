namespace SIRIAUTOPOST.Domain.Enums;

// Serialized as snake_case strings (fb, pending_approval, ...), the same keys the web app uses.

public enum Platform
{
    Fb,
    X,
    Ig,
    Tt,
    Line,
    Th,
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
    /// <summary>Signed up on a paid plan and has not paid yet.</summary>
    Trial,
    /// <summary>A charge failed.</summary>
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
    PlanSettingsChanged,
    PromoCreated,
    PromoToggled,
    Impersonated,
}
