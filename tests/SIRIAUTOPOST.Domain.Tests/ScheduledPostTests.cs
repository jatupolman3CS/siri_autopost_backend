using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class ScheduledPostTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 3, 30, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly Device Phone = Device.Pair(Ws, "Laptop", "Chrome", "2.2", "hash", Now);
    private static readonly SocialAccount Page = SocialAccount.Connect(Ws, Phone, 0);

    private static Post FromSchedule(DateTimeOffset? at = null, Guid? link = null) =>
        Post.FromSchedule(Ws, Page, "baan dee", "  ข้อความ  ", [], at ?? Now.AddHours(2), Now, Guid.NewGuid(), Guid.NewGuid(), link ?? Guid.NewGuid(),
            Post.LinkTargetKey(link ?? Guid.NewGuid()), "2026-10-04T10:00", "https://www.facebook.com/groups/baan.dee", " AB12 ");

    [Fact]
    public void A_scheduled_post_remembers_where_it_came_from()
    {
        var schedule = Guid.NewGuid();
        var cp = Guid.NewGuid();
        var link = Guid.NewGuid();
        var p = Post.FromSchedule(Ws, Page, "baan dee", " text ", [], new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(7)), Now,
            schedule, cp, link, Post.LinkTargetKey(link), "2026-10-05T10:00", "https://www.facebook.com/groups/baan.dee", " AB12 ");

        Assert.Equal(PostStatus.Queued, p.Status);
        Assert.Equal("text", p.Content);
        Assert.Equal(schedule, p.ScheduleId);
        Assert.Equal(cp, p.CollectionPostId);
        Assert.Equal(link, p.LinkId);
        Assert.Equal("link:" + link.ToString("N"), p.TargetKey);
        Assert.Equal("2026-10-05T10:00", p.SlotKey);
        Assert.Equal("https://www.facebook.com/groups/baan.dee", p.TargetUrl);
        Assert.Equal("AB12", p.Code);
        Assert.False(p.IsTest);
        Assert.Equal(TimeSpan.Zero, p.ScheduledAt.Offset);
        Assert.Equal(Platform.Fb, p.Platform);
        Assert.Equal(Page.Id, p.AccountId);
    }

    [Fact]
    public void An_account_member_has_no_link_url_or_code()
    {
        var other = SocialAccount.Create(Ws, Platform.Fb, "@shop", "", "ฟีด");
        var p = Post.FromSchedule(Ws, other, other.DefaultTarget, "text", [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), null,
            Post.AccountTargetKey(other.Id), "2026-10-04T09:00", null, null);

        Assert.Null(p.LinkId);
        Assert.Null(p.TargetUrl);
        Assert.Null(p.Code);
        Assert.Equal("account:" + other.Id.ToString("N"), p.TargetKey);
        Assert.Equal("ฟีด", p.Target);
    }

    [Fact]
    public void A_scheduled_post_checks_its_text_and_workspace()
    {
        Assert.Throws<DomainException>(() => Post.FromSchedule(Ws, Page, "g", " ", [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), null, "k", "s", null, null));
        Assert.Throws<DomainException>(() => Post.FromSchedule(Ws, Page, "g", new string('x', 7001), [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), null, "k", "s", null, null));
        Assert.Throws<DomainException>(() => Post.FromSchedule(Guid.NewGuid(), Page, "g", "x", [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), null, "k", "s", null, null));
        Assert.Throws<DomainException>(() => Post.FromSchedule(Ws, Page, "g", "x", [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), null, "k", "s", new string('x', 301), null));
        Assert.Throws<DomainException>(() => Post.FromSchedule(Ws, Page, "g", "x", [], Now.AddHours(1), Now, Guid.NewGuid(), Guid.NewGuid(), null, "k", "s", null, new string('x', 101)));
    }

    [Fact]
    public void A_test_post_is_due_now_and_marked_as_a_test()
    {
        var p = Post.Test(Ws, Page, "baan dee", "ทดสอบ", [], Now, null, Guid.NewGuid(), "https://www.facebook.com/groups/baan.dee", "AB12");

        Assert.True(p.IsTest);
        Assert.Equal(Now, p.ScheduledAt);
        Assert.Equal(PostStatus.Queued, p.Status);
        Assert.Null(p.ScheduleId);
        Assert.Null(p.SlotKey);
    }

    [Fact]
    public void A_test_post_needs_an_account_with_a_browser()
    {
        var demo = SocialAccount.Create(Ws, Platform.Fb, "Demo", "", "เพจ");
        Assert.Throws<DomainException>(() => Post.Test(Ws, demo, "g", "x", [], Now, null, null, null, null));
    }

    [Fact]
    public void A_queued_post_can_be_skipped_with_a_reason()
    {
        var p = FromSchedule();
        p.Skip(Now, "กลุ่มถูกปิดหรือถูกลบแล้ว");

        Assert.Equal(PostStatus.Skipped, p.Status);
        Assert.Equal("กลุ่มถูกปิดหรือถูกลบแล้ว", p.FailureDetail);
        Assert.False(p.IsOpenError);
    }

    [Fact]
    public void A_waiting_post_can_be_skipped_too_but_nothing_else_can()
    {
        var waiting = FromSchedule();
        waiting.MarkWaiting(Now);
        waiting.Skip(Now, "x");
        Assert.Equal(PostStatus.Skipped, waiting.Status);

        var posting = FromSchedule();
        posting.Claim(Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => posting.Skip(Now, "x"));

        var done = FromSchedule();
        done.Claim(Guid.NewGuid(), Now);
        done.CompletePosted(false, Now);
        Assert.Throws<DomainException>(() => done.Skip(Now, "x"));
    }

    [Fact]
    public void Target_keys_are_stable()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"link:{id:N}", Post.LinkTargetKey(id));
        Assert.Equal($"account:{id:N}", Post.AccountTargetKey(id));
        Assert.Equal(Post.AccountTargetKey(id), Schedule.AccountKey(id));
        Assert.Equal($"{id:N}", Schedule.LinkKey(id));
    }
}
