using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// One posting task: a text (+ media) going to one target of one account at one time.
public class Post : Entity
{
    public const int MaxContentLength = 5000;
    /// <summary>What a composed post may hold: a full-size text plus the group code, the footer and the hashtags.</summary>
    public const int MaxComposedLength = 7000;
    public const int MaxTargetLength = 200;
    public const int MaxTargetUrlLength = 300;
    public const int MaxCodeLength = 100;

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
    /// <summary>What the extension reported when the post failed.</summary>
    public string? FailureDetail { get; private set; }
    /// <summary>The device posting it right now (status Posting).</summary>
    public Guid? ClaimedByDeviceId { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // Posts a schedule generated (or a test post) know where they came from. No foreign keys: the schedule, the
    // collection post and the link may be deleted later, and the claim finds out when a link is gone.
    public Guid? ScheduleId { get; private set; }
    public Guid? CollectionPostId { get; private set; }
    public Guid? LinkId { get; private set; }
    /// <summary>"link:&lt;id&gt;" or "account:&lt;id&gt;": who the post is for within its schedule.</summary>
    public string? TargetKey { get; private set; }
    /// <summary>The planned slot in the schedule's local calendar, "yyyy-MM-ddTHH:mm".</summary>
    public string? SlotKey { get; private set; }
    /// <summary>The group address to post to; posts without one look the group up by name on the account.</summary>
    public string? TargetUrl { get; private set; }
    /// <summary>The group code that was written into the content.</summary>
    public string? Code { get; private set; }
    /// <summary>Sent from the test page: one real post, due at once.</summary>
    public bool IsTest { get; private set; }
    /// <summary>
    /// The address of the post on Facebook, as the extension saw it right after posting (empty when it could not find
    /// it, or the group holds the post for approval). A bump opens this address.
    /// </summary>
    public string? PostUrl { get; private set; }

    public const int MaxPostUrlLength = 500;

    /// <summary>
    /// When somebody asked for this post to go out now (the timeline's "post now", the manual rerun of a failed post). A
    /// rushed post is due at that moment and the claim hands it out before every other due post of its account.
    /// </summary>
    public DateTimeOffset? RushedAt { get; private set; }

    private Post() { } // EF Core

    public static string LinkTargetKey(Guid linkId) => "link:" + linkId.ToString("N");

    public static string AccountTargetKey(Guid accountId) => "account:" + accountId.ToString("N");

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

    /// <summary>
    /// One post of a schedule's run. The content is already composed (spintax, code, footer, hashtags). The caller
    /// decides which slots are in the future; the account's login state is checked when the post is claimed.
    /// </summary>
    public static Post FromSchedule(
        Guid workspaceId, SocialAccount account, string target, string content, IEnumerable<Guid> mediaIds, DateTimeOffset at,
        DateTimeOffset now, Guid scheduleId, Guid collectionPostId, Guid? linkId, string targetKey, string slotKey,
        string? targetUrl, string? code)
    {
        if (account.WorkspaceId != workspaceId) throw new DomainException("บัญชีนี้ไม่ได้อยู่ในเวิร์กสเปซนี้");
        var post = Compose(workspaceId, account, target, content, mediaIds, at.ToUniversalTime(), now, collectionPostId, linkId, targetUrl, code);
        post.ScheduleId = scheduleId;
        post.TargetKey = targetKey;
        post.SlotKey = slotKey;
        return post;
    }

    /// <summary>A real post from the test page, due now. The account must be connected through a browser.</summary>
    public static Post Test(
        Guid workspaceId, SocialAccount account, string target, string content, IEnumerable<Guid> mediaIds, DateTimeOffset now,
        Guid? collectionPostId, Guid? linkId, string? targetUrl, string? code)
    {
        if (account.WorkspaceId != workspaceId) throw new DomainException("บัญชีนี้ไม่ได้อยู่ในเวิร์กสเปซนี้");
        if (!account.IsConnected) throw new DomainException($"บัญชี {account.Name} ยังไม่ได้เชื่อมกับเครื่อง จับคู่เครื่องในหน้าทีมและเวิร์กสเปซก่อน");
        var post = Compose(workspaceId, account, target, content, mediaIds, now.ToUniversalTime(), now, collectionPostId, linkId, targetUrl, code);
        post.IsTest = true;
        return post;
    }

    private static Post Compose(
        Guid workspaceId, SocialAccount account, string target, string content, IEnumerable<Guid> mediaIds, DateTimeOffset at,
        DateTimeOffset now, Guid? collectionPostId, Guid? linkId, string? targetUrl, string? code)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0) throw new DomainException("กรุณาใส่ข้อความโพสต์");
        if (text.Length > MaxComposedLength) throw new DomainException($"ข้อความโพสต์ยาวเกิน {MaxComposedLength} ตัวอักษร");
        var url = string.IsNullOrWhiteSpace(targetUrl) ? null : targetUrl.Trim();
        if (url is { Length: > MaxTargetUrlLength }) throw new DomainException($"ลิงก์ยาวเกิน {MaxTargetUrlLength} ตัวอักษร");
        var c = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        if (c is { Length: > MaxCodeLength }) throw new DomainException($"รหัสกลุ่มยาวเกิน {MaxCodeLength} ตัวอักษร");
        var t = (target ?? "").Trim();
        return new Post
        {
            WorkspaceId = workspaceId,
            AccountId = account.Id,
            Platform = account.Platform,
            Target = t.Length > MaxTargetLength ? t[..MaxTargetLength] : t,
            Content = text,
            MediaIds = mediaIds.Distinct().ToList(),
            ScheduledAt = at,
            Status = PostStatus.Queued,
            CollectionPostId = collectionPostId,
            LinkId = linkId,
            TargetUrl = url,
            Code = c,
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
        FailureDetail = null;
        ErrorDismissed = false;
        ScheduledAt = now.AddMinutes(15).ToUniversalTime();
        RushedAt = null; // a retry waits its turn like any other post
        UpdatedAt = now;
    }

    /// <summary>
    /// "Post now": jumps the queue. The post leaves its slot (it is due this moment, so nothing is left to run at the old
    /// time, and the slot key keeps the schedule from queueing another one there) and the claim hands it out before the
    /// other due posts. The anti-ban gap between two posts of the account still applies. A post that went out, is being
    /// posted or waits for the group's approval cannot be sent again.
    /// </summary>
    public void RunNow(DateTimeOffset now)
    {
        if (Status is not (PostStatus.Queued or PostStatus.Waiting or PostStatus.Failed or PostStatus.Skipped))
            throw new DomainException("สั่งโพสต์เดี๋ยวนี้ได้เฉพาะโพสต์ที่รอโพสต์ ล้มเหลว หรือถูกข้าม");
        Status = PostStatus.Queued;
        FailureCode = null;
        FailureDetail = null;
        ErrorDismissed = false;
        ScheduledAt = now.ToUniversalTime();
        RushedAt = now.ToUniversalTime();
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

    /// <summary>The extension came back: waiting posts go back to the queue (sent next), or are skipped under the skip policy.</summary>
    public void ResolveWaiting(bool skip, DateTimeOffset now)
    {
        if (Status != PostStatus.Waiting) return;
        Status = skip ? PostStatus.Skipped : PostStatus.Queued;
        UpdatedAt = now;
    }

    public const int MaxDetailLength = 500;
    /// <summary>A device that does not report back within this time is assumed to have failed.</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A device takes the post to publish it now.</summary>
    public void Claim(Guid deviceId, DateTimeOffset now)
    {
        if (Status != PostStatus.Queued) throw new DomainException("โพสต์นี้ไม่ได้อยู่ในคิวแล้ว");
        Status = PostStatus.Posting;
        ClaimedByDeviceId = deviceId;
        ClaimedAt = now;
        UpdatedAt = now;
    }

    public bool ClaimExpired(DateTimeOffset now) =>
        Status == PostStatus.Posting && ClaimedAt is { } at && now - at > ClaimTimeout;

    /// <summary>The device posted it; a group that needs admin approval leaves it pending.</summary>
    public void CompletePosted(bool awaitingApproval, DateTimeOffset now)
    {
        if (Status != PostStatus.Posting) throw new DomainException("โพสต์นี้ไม่ได้อยู่ระหว่างโพสต์");
        Status = awaitingApproval ? PostStatus.Pending : PostStatus.Success;
        FailureCode = awaitingApproval ? Enums.FailureCode.PendingApproval : null;
        PublishedAt = now.ToUniversalTime();
        ClaimedByDeviceId = null;
        UpdatedAt = now;
    }

    /// <summary>Remembers where the post went up on Facebook; anything but a Facebook address is ignored.</summary>
    public void RecordPostUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || u.Length > MaxPostUrlLength) return;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        var host = uri.Host.ToLowerInvariant();
        if (host != "facebook.com" && !host.EndsWith(".facebook.com") && host != "fb.com" && !host.EndsWith(".fb.com")) return;
        PostUrl = u;
    }

    /// <summary>Could not be posted (by the device, or refused before it was handed out).</summary>
    public void Fail(FailureCode code, string? detail, DateTimeOffset now)
    {
        if (Status is not (PostStatus.Posting or PostStatus.Queued or PostStatus.Waiting))
            throw new DomainException("โพสต์นี้ไม่ได้อยู่ในคิวหรือระหว่างโพสต์");
        Status = PostStatus.Failed;
        FailureCode = code;
        FailureDetail = Cut(detail);
        ErrorDismissed = false;
        ClaimedByDeviceId = null;
        UpdatedAt = now;
    }

    /// <summary>
    /// Left out before it was sent (a link that was switched off or deleted, a group's daily cap): the reason is kept
    /// in the failure detail. Only posts still waiting can be skipped.
    /// </summary>
    public void Skip(DateTimeOffset now, string reason)
    {
        if (Status is not (PostStatus.Queued or PostStatus.Waiting)) throw new DomainException("ข้ามได้เฉพาะโพสต์ที่ยังรอโพสต์");
        Status = PostStatus.Skipped;
        FailureDetail = Cut(reason);
        UpdatedAt = now;
    }

    /// <summary>Too late to post under the workspace's offline policy.</summary>
    public void SkipLate(DateTimeOffset now)
    {
        if (Status != PostStatus.Queued) return;
        Status = PostStatus.Skipped;
        UpdatedAt = now;
    }

    private static string? Cut(string? s)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > MaxDetailLength ? t[..MaxDetailLength] : t;
    }
}
