using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// One posting task: a text (+ media) going to one target of one account at one time.
public class Post : Entity
{
    public const int MaxContentLength = 5000;
    public const int MaxTargetLength = 200;

    public Guid WorkspaceId { get; private set; }
    public Guid AccountId { get; private set; }
    public Platform Platform { get; private set; }
    public string Target { get; private set; } = "";
    public string Content { get; private set; } = "";
    public List<Guid> MediaIds { get; private set; } = [];
    public DateTimeOffset ScheduledAt { get; private set; }
    public PostStatus Status { get; private set; }
    public FailureCode? FailureCode { get; private set; }
    /// <summary>The user acknowledged the error report (skip / dismiss).</summary>
    public bool ErrorDismissed { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Post() { } // EF Core

    public static Post Schedule(
        Guid workspaceId, SocialAccount account, string target, string content,
        IEnumerable<Guid> mediaIds, DateTimeOffset at, DateTimeOffset now)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0) throw new DomainException("กรุณาใส่ข้อความโพสต์");
        if (text.Length > MaxContentLength) throw new DomainException($"ข้อความโพสต์ยาวเกิน {MaxContentLength} ตัวอักษร");
        if (at <= now) throw new DomainException("เวลาที่เลือกผ่านไปแล้ว กรุณาเลือกเวลาในอนาคต");
        if (!account.CanPost) throw new DomainException($"บัญชี {account.Name} ต้องเข้าสู่ระบบใหม่ก่อน");
        if (account.WorkspaceId != workspaceId) throw new DomainException("บัญชีนี้ไม่ได้อยู่ในเวิร์กสเปซนี้");
        return new Post
        {
            WorkspaceId = workspaceId,
            AccountId = account.Id,
            Platform = account.Platform,
            Target = target.Length > MaxTargetLength ? target[..MaxTargetLength] : target,
            Content = text,
            MediaIds = mediaIds.Distinct().ToList(),
            ScheduledAt = at.ToUniversalTime(), // PostgreSQL timestamptz only stores UTC offsets
            Status = PostStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>History/error rows created by the extension (and the demo data).</summary>
    public static Post Record(
        Guid workspaceId, SocialAccount account, string target, string content, DateTimeOffset at,
        PostStatus status, FailureCode? failure, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId,
            AccountId = account.Id,
            Platform = account.Platform,
            Target = target,
            Content = content,
            ScheduledAt = at.ToUniversalTime(),
            Status = status,
            FailureCode = failure,
            PublishedAt = status is PostStatus.Success or PostStatus.Pending ? at.ToUniversalTime() : null,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public bool IsOpenError => !ErrorDismissed && Status is PostStatus.Failed or PostStatus.Pending;

    public void EnsureDeletable()
    {
        if (Status != PostStatus.Queued) throw new DomainException("ลบได้เฉพาะโพสต์ที่ยังรอโพสต์");
    }

    /// <summary>Back into the queue, 15 minutes from now.</summary>
    public void Retry(DateTimeOffset now)
    {
        if (Status != PostStatus.Failed) throw new DomainException("ลองใหม่ได้เฉพาะโพสต์ที่ล้มเหลว");
        Status = PostStatus.Queued;
        FailureCode = null;
        ErrorDismissed = false;
        ScheduledAt = now.AddMinutes(15).ToUniversalTime();
        UpdatedAt = now;
    }

    /// <summary>Skip a failed post, or acknowledge one waiting for group approval.</summary>
    public void DismissError(DateTimeOffset now)
    {
        if (!IsOpenError) throw new DomainException("ไม่มีข้อผิดพลาดค้างอยู่สำหรับโพสต์นี้");
        if (Status == PostStatus.Failed) Status = PostStatus.Skipped;
        ErrorDismissed = true;
        UpdatedAt = now;
    }

    public void MarkWaiting(DateTimeOffset now)
    {
        if (Status != PostStatus.Queued) return;
        Status = PostStatus.Waiting;
        UpdatedAt = now;
    }

    /// <summary>The extension came back: waiting posts are sent, or skipped under the skip policy.</summary>
    public void ResolveWaiting(bool skip, DateTimeOffset now)
    {
        if (Status != PostStatus.Waiting) return;
        Status = skip ? PostStatus.Skipped : PostStatus.Success;
        PublishedAt = skip ? null : now.ToUniversalTime();
        UpdatedAt = now;
    }
}
