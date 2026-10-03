using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class PostTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 3, 30, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly SocialAccount Page = SocialAccount.Create(Ws, Platform.Fb, "Page", "", "เพจ", groups: ["A", "B"]);

    private static Post Queued() => Post.Schedule(Ws, Page, "A", "  hello  ", [], Now.AddHours(1), Now);

    private static Post Failed() =>
        Post.Record(Ws, Page, "A", "hello", Now.AddHours(-1), PostStatus.Failed, FailureCode.RateLimit, Now);

    [Fact]
    public void Schedule_trims_content_queues_and_stores_utc()
    {
        var at = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.FromHours(7));
        var p = Post.Schedule(Ws, Page, "A", "  hello  ", [], at, Now);

        Assert.Equal("hello", p.Content);
        Assert.Equal(PostStatus.Queued, p.Status);
        Assert.Equal(TimeSpan.Zero, p.ScheduledAt.Offset);
        Assert.Equal(at, p.ScheduledAt);
    }

    [Fact]
    public void Schedule_rejects_empty_text_and_past_times()
    {
        Assert.Throws<DomainException>(() => Post.Schedule(Ws, Page, "A", " ", [], Now.AddHours(1), Now));
        Assert.Throws<DomainException>(() => Post.Schedule(Ws, Page, "A", "hi", [], Now, Now));
    }

    [Fact]
    public void Schedule_refuses_accounts_that_must_sign_in_again_or_belong_elsewhere()
    {
        var expired = SocialAccount.Create(Ws, Platform.Tt, "tt", "", "โปรไฟล์", AccountHealth.Relogin);
        Assert.Throws<DomainException>(() => Post.Schedule(Ws, expired, "x", "hi", [], Now.AddHours(1), Now));
        Assert.Throws<DomainException>(() => Post.Schedule(Guid.NewGuid(), Page, "A", "hi", [], Now.AddHours(1), Now));
    }

    [Fact]
    public void Only_queued_posts_can_be_deleted()
    {
        Queued().EnsureDeletable();
        Assert.Throws<DomainException>(() => Failed().EnsureDeletable());
    }

    [Fact]
    public void Retry_requeues_a_failed_post_15_minutes_from_now()
    {
        var p = Failed();
        p.Retry(Now);

        Assert.Equal(PostStatus.Queued, p.Status);
        Assert.Null(p.FailureCode);
        Assert.Equal(Now.AddMinutes(15), p.ScheduledAt);
        Assert.Throws<DomainException>(() => Queued().Retry(Now));
    }

    [Fact]
    public void Dismissing_skips_a_failed_post_and_closes_the_report()
    {
        var p = Failed();
        Assert.True(p.IsOpenError);
        p.DismissError(Now);
        Assert.Equal(PostStatus.Skipped, p.Status);
        Assert.False(p.IsOpenError);
    }

    [Fact]
    public void Dismissing_an_approval_notice_keeps_it_pending()
    {
        var p = Post.Record(Ws, Page, "A", "hi", Now.AddHours(-2), PostStatus.Pending, FailureCode.PendingApproval, Now);
        p.DismissError(Now);
        Assert.Equal(PostStatus.Pending, p.Status);
        Assert.False(p.IsOpenError);
    }

    [Theory]
    [InlineData(true, PostStatus.Skipped)]
    [InlineData(false, PostStatus.Queued)]
    public void Waiting_posts_resolve_by_policy(bool skip, PostStatus expected)
    {
        var p = Queued();
        p.MarkWaiting(Now);
        Assert.Equal(PostStatus.Waiting, p.Status);
        p.ResolveWaiting(skip, Now);
        Assert.Equal(expected, p.Status);
    }
}
