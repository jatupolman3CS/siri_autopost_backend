using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Entities;

// One post to publish in one Facebook group.
public class Post : Entity
{
    public const int MaxContentLength = 5000;

    public string Content { get; private set; } = "";
    public GroupUrl GroupUrl { get; private set; } = null!;
    public PostStatus Status { get; private set; } = PostStatus.Draft;
    public DateTimeOffset? ScheduledAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Post() { } // EF Core

    public static Post Create(string content, GroupUrl groupUrl, DateTimeOffset now)
    {
        var post = new Post { GroupUrl = groupUrl, CreatedAt = now };
        post.SetContent(content, now);
        return post;
    }

    public void UpdateContent(string content, DateTimeOffset now)
    {
        EnsureEditable();
        SetContent(content, now);
    }

    public void Schedule(DateTimeOffset at, DateTimeOffset now)
    {
        EnsureEditable();
        if (at <= now) throw new DomainException("เวลาที่ตั้งโพสต์ต้องเป็นเวลาในอนาคต");
        ScheduledAt = at.ToUniversalTime(); // PostgreSQL timestamptz only stores UTC offsets
        Status = PostStatus.Scheduled;
        UpdatedAt = now;
    }

    public void MarkPublished(DateTimeOffset now)
    {
        if (Status == PostStatus.Published) throw new DomainException("โพสต์นี้เผยแพร่แล้ว");
        Status = PostStatus.Published;
        PublishedAt = now;
        FailureReason = null;
        UpdatedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status == PostStatus.Published) throw new DomainException("โพสต์นี้เผยแพร่แล้ว");
        Status = PostStatus.Failed;
        FailureReason = reason;
        UpdatedAt = now;
    }

    private void SetContent(string content, DateTimeOffset now)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0) throw new DomainException("กรุณาใส่ข้อความโพสต์");
        if (text.Length > MaxContentLength) throw new DomainException($"ข้อความโพสต์ยาวเกิน {MaxContentLength} ตัวอักษร");
        Content = text;
        UpdatedAt = now;
    }

    private void EnsureEditable()
    {
        if (Status == PostStatus.Published) throw new DomainException("แก้ไขโพสต์ที่เผยแพร่แล้วไม่ได้");
    }
}
