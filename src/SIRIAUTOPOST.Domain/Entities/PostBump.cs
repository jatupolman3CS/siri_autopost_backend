using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

/// <summary>
/// One bump: a comment the browser that posted something makes on that post some hours later, so it comes back to the top
/// of the group. It is made when the post is reported as posted (and its address is known), for a schedule that bumps and a
/// workspace whose owner's plan includes bumping, and the same browser claims it when it is due. It is not a post: it does
/// not count toward the day's limits, the calendar or the reports.
/// </summary>
public class PostBump : Entity
{
    /// <summary>A bump that is not made within this long after it was due is dropped: a comment days late is no bump.</summary>
    public static readonly TimeSpan MaxLateness = TimeSpan.FromHours(12);
    /// <summary>A browser that does not report back within this time is assumed to have failed.</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);
    public const int MaxDetailLength = 500;

    public Guid WorkspaceId { get; private set; }
    /// <summary>The post being bumped.</summary>
    public Guid PostId { get; private set; }
    public Guid? ScheduleId { get; private set; }
    /// <summary>The account (and so the browser) that posted it: only that login can comment as the same person.</summary>
    public Guid AccountId { get; private set; }
    /// <summary>The address of the post on Facebook.</summary>
    public string Url { get; private set; } = "";
    /// <summary>The group or page the post is in, for the log lines.</summary>
    public string Target { get; private set; } = "";
    /// <summary>The comment (spintax already resolved).</summary>
    public string Text { get; private set; } = "";
    public List<Guid> MediaIds { get; private set; } = [];
    /// <summary>1 for the first bump of a post, 2 for the second...</summary>
    public int Round { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public BumpStatus Status { get; private set; }
    public Guid? ClaimedByDeviceId { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? DoneAt { get; private set; }
    public string? FailureDetail { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private PostBump() { } // EF Core

    public static PostBump Create(
        Post post, string url, string text, IEnumerable<Guid> mediaIds, int round, DateTimeOffset dueAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new DomainException("โพสต์นี้ยังไม่มีลิงก์ให้ดัน");
        return new PostBump
        {
            WorkspaceId = post.WorkspaceId,
            PostId = post.Id,
            ScheduleId = post.ScheduleId,
            AccountId = post.AccountId,
            Url = url,
            Target = post.Target,
            Text = (text ?? "").Trim(),
            MediaIds = mediaIds.Distinct().ToList(),
            Round = round,
            DueAt = dueAt.ToUniversalTime(),
            Status = BumpStatus.Queued,
            CreatedAt = now,
        };
    }

    public void Claim(Guid deviceId, DateTimeOffset now)
    {
        if (Status != BumpStatus.Queued) throw new DomainException("การดันนี้ไม่ได้อยู่ในคิวแล้ว");
        Status = BumpStatus.Posting;
        ClaimedByDeviceId = deviceId;
        ClaimedAt = now;
    }

    public bool ClaimExpired(DateTimeOffset now) => Status == BumpStatus.Posting && ClaimedAt is { } at && now - at > ClaimTimeout;

    public bool TooLate(DateTimeOffset now) => Status == BumpStatus.Queued && now - DueAt > MaxLateness;

    public void Complete(DateTimeOffset now)
    {
        if (Status != BumpStatus.Posting) throw new DomainException("การดันนี้ไม่ได้อยู่ระหว่างทำงาน");
        Status = BumpStatus.Done;
        DoneAt = now;
        ClaimedByDeviceId = null;
    }

    public void Fail(string? detail, DateTimeOffset now)
    {
        if (Status is not (BumpStatus.Posting or BumpStatus.Queued)) throw new DomainException("การดันนี้ไม่ได้อยู่ในคิวหรือระหว่างทำงาน");
        Status = BumpStatus.Failed;
        FailureDetail = Cut(detail);
        DoneAt = now;
        ClaimedByDeviceId = null;
    }

    /// <summary>Left out (too late, the browser is gone, the group was switched off).</summary>
    public void Skip(string reason, DateTimeOffset now)
    {
        if (Status is not (BumpStatus.Posting or BumpStatus.Queued)) return;
        Status = BumpStatus.Skipped;
        FailureDetail = Cut(reason);
        DoneAt = now;
        ClaimedByDeviceId = null;
    }

    private static string? Cut(string? s)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > MaxDetailLength ? t[..MaxDetailLength] : t;
    }
}
