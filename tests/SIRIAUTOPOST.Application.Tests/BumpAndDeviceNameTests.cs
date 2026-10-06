using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Application.Tests;

public class BumpAndDeviceNameTests
{
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);

    // ---------- device names ----------

    [Theory]
    [InlineData("Chrome", new string[0], "Chrome")]
    [InlineData("Chrome", new[] { "Chrome" }, "Chrome (2)")]
    [InlineData("chrome", new[] { "Chrome" }, "chrome (2)")] // compared without regard to case
    [InlineData("Chrome", new[] { "Chrome", "Chrome (2)" }, "Chrome (3)")]
    [InlineData("  Chrome ", new[] { "Chrome" }, "Chrome (2)")]
    [InlineData("", new string[0], "เครื่องไม่มีชื่อ")]
    public void A_device_name_is_made_unique_in_its_workspace(string desired, string[] taken, string expected) =>
        Assert.Equal(expected, Device.UniqueName(desired, taken));

    [Fact]
    public void A_long_name_stays_within_the_limit_when_a_suffix_is_added()
    {
        var name = new string('ก', Device.MaxNameLength);
        var unique = Device.UniqueName(name, [name]);
        Assert.Equal(Device.MaxNameLength, unique.Length);
        Assert.EndsWith(" (2)", unique);
    }

    [Fact]
    public void Names_are_the_same_when_they_match_after_trimming_whatever_their_case() =>
        Assert.True(Device.SameName(" Shop PC ", "shop pc"));

    // ---------- the post's address ----------

    private static Post PostedPost(string? url = null)
    {
        var account = SocialAccount.Connect(Ws, Device.Pair(Ws, "PC", "Chrome", "2.3", "h", Now), 0);
        var post = Post.FromSchedule(Ws, account, "กลุ่ม", "ข้อความ", [], Now.AddMinutes(1), Now, Guid.NewGuid(), Guid.NewGuid(), null, "link:x", "slot", "https://www.facebook.com/groups/g", null);
        post.Claim(account.DeviceId!.Value, Now);
        post.CompletePosted(false, Now.AddMinutes(2));
        post.RecordPostUrl(url);
        return post;
    }

    [Theory]
    [InlineData("https://www.facebook.com/groups/g/posts/123/", true)]
    [InlineData("https://web.facebook.com/groups/g/permalink/123/", true)]
    [InlineData("https://fb.com/groups/g/posts/1", true)]
    [InlineData("http://m.facebook.com/p/x", true)]
    [InlineData("https://example.com/groups/g/posts/123/", false)]
    [InlineData("https://facebook.com.evil.com/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_facebook_address_is_kept_as_the_posts_address(string? url, bool kept) =>
        Assert.Equal(kept, PostedPost(url).PostUrl is not null);

    // ---------- the bump plan ----------

    [Fact]
    public void A_bump_plan_is_checked_and_tidied()
    {
        var a = Guid.NewGuid();
        var plan = new BumpPlan { Rounds = 2, Text = "  ดันค่ะ ", MediaIds = [a, a], ImagesEach = 3 }.Cleaned();
        Assert.Equal((2, "ดันค่ะ", 1, 1), (plan.Rounds, plan.Text, plan.MediaIds.Count, plan.ImagesEach)); // never more images than were picked

        Assert.Equal(0, new BumpPlan { Rounds = 1, ImagesEach = 3 }.Cleaned().ImagesEach); // no pool, no images
        Assert.Throws<DomainException>(() => new BumpPlan { Rounds = 0 }.Cleaned());
        Assert.Throws<DomainException>(() => new BumpPlan { Rounds = BumpPlan.MaxRounds + 1 }.Cleaned());
        Assert.Throws<DomainException>(() => new BumpPlan { ImagesEach = BumpPlan.MaxImagesEach + 1 }.Cleaned());
        Assert.Throws<DomainException>(() => new BumpPlan { Text = new string('ก', BumpPlan.MaxTextLength + 1) }.Cleaned());
        Assert.Throws<DomainException>(() => new BumpPlan { MediaIds = Enumerable.Range(0, BumpPlan.MaxPool + 1).Select(_ => Guid.NewGuid()).ToList() }.Cleaned());
    }

    private static Schedule BumpingSchedule(int hours, BumpPlan? plan) =>
        Schedule.Create(Ws, "ตาราง", Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["10:00"], 6, null, DateOnly.FromDateTime(Now.UtcDateTime), null,
            PostOrder.Rotate, null, null, 3, hours, 0, null, 0, Now, bump: plan);

    [Fact]
    public void The_bump_hours_are_limited_to_the_options()
    {
        foreach (var hours in new[] { 0, 1, 2, 3, 6, 12, 24 }) Assert.Equal(hours, BumpingSchedule(hours, null).BumpHours);
        Assert.Throws<DomainException>(() => BumpingSchedule(5, null));
    }

    [Fact]
    public void A_post_makes_one_bump_per_round_spaced_by_the_bump_hours_with_their_own_text_and_images()
    {
        var pool = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var schedule = BumpingSchedule(2, new BumpPlan { Rounds = 3, Text = "{ก|ข}", MediaIds = [.. pool], ImagesEach = 2 });
        var post = PostedPost("https://www.facebook.com/groups/g/posts/123/");
        var rnd = new Random(7);

        var bumps = BumpPlanner.Plan(post, schedule, Now.AddMinutes(2), rnd.NextDouble);

        Assert.Equal([1, 2, 3], bumps.Select(b => b.Round));
        Assert.All(bumps, b =>
        {
            Assert.Equal((post.Id, post.PostUrl, BumpStatus.Queued), (b.PostId, b.Url, b.Status));
            Assert.Contains(b.Text, new[] { "ก", "ข" });
            Assert.Equal(2, b.MediaIds.Count);
            Assert.Equal(2, b.MediaIds.Distinct().Count());
            Assert.All(b.MediaIds, id => Assert.Contains(id, pool));
        });
        // Each is `BumpHours` after the one before (and not more than the jitter later than that).
        for (var i = 0; i < 3; i++)
        {
            var earliest = post.PublishedAt!.Value.AddHours(2 * (i + 1));
            Assert.InRange(bumps[i].DueAt, earliest, earliest.AddMinutes(BumpPlanner.JitterMinutes));
        }
    }

    [Fact]
    public void Nothing_is_bumped_without_the_posts_address_for_a_schedule_that_does_not_bump_or_for_a_test()
    {
        var schedule = BumpingSchedule(1, null);
        Assert.Empty(BumpPlanner.Plan(PostedPost(null), schedule, Now, () => 0.5));
        Assert.Empty(BumpPlanner.Plan(PostedPost("https://www.facebook.com/groups/g/posts/1/"), BumpingSchedule(0, null), Now, () => 0.5));
        Assert.Empty(BumpPlanner.Plan(PostedPost("https://www.facebook.com/groups/g/posts/1/"), null, Now, () => 0.5));

        var account = SocialAccount.Connect(Ws, Device.Pair(Ws, "PC", "Chrome", "2.3", "h", Now), 0);
        var test = Post.Test(Ws, account, "กลุ่ม", "ข้อความ", [], Now, null, null, "https://www.facebook.com/groups/g", null);
        test.Claim(account.DeviceId!.Value, Now);
        test.CompletePosted(false, Now);
        test.RecordPostUrl("https://www.facebook.com/groups/g/posts/1/");
        Assert.Empty(BumpPlanner.Plan(test, schedule, Now, () => 0.5));
    }

    [Fact]
    public void A_bump_without_text_or_images_says_a_short_default_and_one_with_images_only_says_nothing()
    {
        Assert.Contains(BumpPlanner.TextFor(new BumpPlan(), () => 0.0), new[] { "ขึ้นๆ ค่ะ", "ดันหน่อยค่ะ", "ยังมีของพร้อมส่งนะคะ", "สนใจทักแชทได้เลยค่ะ" });
        Assert.Equal("", BumpPlanner.TextFor(new BumpPlan { MediaIds = [Guid.NewGuid()], ImagesEach = 1 }, () => 0.0));
        Assert.Equal("ดัน", BumpPlanner.TextFor(new BumpPlan { Text = "ดัน" }, () => 0.0));
    }

    // ---------- a bump's life ----------

    [Fact]
    public void A_bump_is_claimed_then_done_or_failed_and_one_that_is_far_overdue_is_too_late()
    {
        var post = PostedPost("https://www.facebook.com/groups/g/posts/1/");
        var bump = PostBump.Create(post, post.PostUrl!, "ดัน", [], 1, Now, Now);

        Assert.False(bump.TooLate(Now.AddHours(11)));
        Assert.True(bump.TooLate(Now.AddHours(13)));
        bump.Claim(Guid.NewGuid(), Now);
        Assert.False(bump.ClaimExpired(Now.AddMinutes(14)));
        Assert.True(bump.ClaimExpired(Now.AddMinutes(16)));
        Assert.Throws<DomainException>(() => bump.Claim(Guid.NewGuid(), Now)); // only a queued bump

        bump.Complete(Now.AddMinutes(1));
        Assert.Equal(BumpStatus.Done, bump.Status);
        Assert.Throws<DomainException>(() => bump.Fail("x", Now)); // finished: nothing changes

        var other = PostBump.Create(post, post.PostUrl!, "ดัน", [], 2, Now, Now);
        other.Skip("ตารางโพสต์ถูกลบแล้ว", Now);
        Assert.Equal((BumpStatus.Skipped, "ตารางโพสต์ถูกลบแล้ว"), (other.Status, other.FailureDetail));
        other.Skip("again", Now); // a finished bump is left as it is
        Assert.Equal("ตารางโพสต์ถูกลบแล้ว", other.FailureDetail);
    }

    // ---------- packages ----------

    [Fact]
    public void The_packages_list_what_each_one_includes()
    {
        Assert.Empty(PlanFeatures.For(PlanKey.Free));
        Assert.Empty(PlanFeatures.For(PlanKey.Basic));
        Assert.Equal([PlanFeatures.AdvancedAntiBan, PlanFeatures.Notifications, PlanFeatures.AutoReply, PlanFeatures.Ai], PlanFeatures.For(PlanKey.Pro));
        Assert.Equal(PlanFeatures.All, PlanFeatures.For(PlanKey.Agency));
        Assert.DoesNotContain(PlanFeatures.Bump, PlanFeatures.For(PlanKey.Pro));

        var pro = User.Create("a@b.co", "A", UserRole.User, PlanKey.Pro, Now);
        var premium = User.Create("c@d.co", "C", UserRole.User, PlanKey.Agency, Now);
        Assert.Equal((true, false, true, false), (pro.HasAi, pro.HasBump, pro.HasAdvancedAntiBan, pro.HasClientReports));
        Assert.Equal((true, true, true, true), (premium.HasAi, premium.HasBump, premium.HasAdvancedAntiBan, premium.HasClientReports));
    }

    [Fact]
    public void A_plan_edit_keeps_every_limit_above_zero_or_empty()
    {
        var free = PlanSetting.Defaults.Single(p => p.Key == PlanKey.Free);
        Assert.Equal((10, 20, 20), (free.Groups, free.Images, free.LibraryPosts));
        free.Update(0, 1, 10, 1, 1, groups: 15, images: null, libraryPosts: 30);
        Assert.Equal((15, null, 30), (free.Groups, free.Images, free.LibraryPosts));
        Assert.Throws<DomainException>(() => free.Update(0, 1, 10, 1, 1, groups: 0));
    }
}
