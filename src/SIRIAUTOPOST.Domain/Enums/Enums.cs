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
