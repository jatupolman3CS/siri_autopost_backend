using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class CollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();

    private static PostCollection Collection(bool approval = false, string name = "โปรโมชัน")
    {
        var c = PostCollection.Create(Ws, name, "รายละเอียด", Now);
        if (approval) c.Update(c.Name, c.Description, null, new CollectionSettings { RequireApproval = true });
        return c;
    }

    [Fact]
    public void A_collection_needs_a_name_and_keeps_its_limits()
    {
        var c = Collection();
        Assert.Equal("ph-folder", c.Icon);
        Assert.False(c.Settings.RequireApproval);
        Assert.True(c.Settings.Shuffle);
        Assert.Equal(FooterPosition.End, c.Settings.FooterPos);
        Assert.Equal(WatermarkPosition.Br, c.Settings.WatermarkPos);

        Assert.Throws<DomainException>(() => PostCollection.Create(Ws, "  ", null, Now));
        Assert.Throws<DomainException>(() => PostCollection.Create(Ws, new string('x', 121), null, Now));
        Assert.Throws<DomainException>(() => PostCollection.Create(Ws, "x", new string('x', 301), Now));
        Assert.Equal("", PostCollection.Create(Ws, "x", null, Now).Description);
    }

    [Fact]
    public void Update_changes_the_settings_and_validates_them()
    {
        var c = Collection();
        c.Update(" ใหม่ ", "d", "ph-star", new CollectionSettings { Hashtags = " #a #b ", Footer = "ทักแชท", FooterPos = FooterPosition.Top });

        Assert.Equal("ใหม่", c.Name);
        Assert.Equal("ph-star", c.Icon);
        Assert.Equal("#a #b", c.Settings.Hashtags);
        Assert.Equal(FooterPosition.Top, c.Settings.FooterPos);

        c.Update("ใหม่", "d", null, new CollectionSettings());
        Assert.Equal("ph-star", c.Icon); // no icon keeps it

        Assert.Throws<DomainException>(() => c.Update("x", "", null, new CollectionSettings { Hashtags = new string('#', 501) }));
        Assert.Throws<DomainException>(() => c.Update("x", "", null, new CollectionSettings { PageTags = new string('x', 1001) }));
        Assert.Throws<DomainException>(() => c.Update("x", "", null, new CollectionSettings { Footer = new string('x', 1001) }));
        Assert.Throws<DomainException>(() => c.Update("x", "", new string('x', 41), new CollectionSettings()));
    }

    [Fact]
    public void A_new_post_is_approved_unless_the_collection_requires_approval()
    {
        Assert.Equal(PostApproval.Approved, CollectionPost.Create(Collection(), "hello", [], Now).Approval);
        Assert.Equal(PostApproval.Draft, CollectionPost.Create(Collection(approval: true), "hello", [], Now).Approval);
    }

    [Fact]
    public void A_post_needs_text_and_keeps_its_limits()
    {
        var c = Collection();
        Assert.Throws<DomainException>(() => CollectionPost.Create(c, "  ", [], Now));
        Assert.Throws<DomainException>(() => CollectionPost.Create(c, new string('x', 5001), [], Now));
        Assert.Throws<DomainException>(() => CollectionPost.Create(c, "x", Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()), Now));

        var media = Guid.NewGuid();
        var p = CollectionPost.Create(c, "  hello  ", [media, media], Now);
        Assert.Equal("hello", p.Text);
        Assert.Equal([media], p.MediaIds);
        Assert.Equal(Ws, p.WorkspaceId);
    }

    [Fact]
    public void Approval_goes_draft_to_pending_to_approved()
    {
        var c = Collection(approval: true);
        var p = CollectionPost.Create(c, "hello", [], Now);

        p.RequestApproval(Now);
        Assert.Equal(PostApproval.Pending, p.Approval);

        p.Approve(Now.AddMinutes(1));
        Assert.Equal(PostApproval.Approved, p.Approval);
        Assert.Equal(Now.AddMinutes(1), p.UpdatedAt);
    }

    [Fact]
    public void A_rejected_post_goes_back_to_draft()
    {
        var p = CollectionPost.Create(Collection(approval: true), "hello", [], Now);
        p.RequestApproval(Now);
        p.Reject(Now);
        Assert.Equal(PostApproval.Draft, p.Approval);
    }

    [Fact]
    public void Approval_steps_cannot_be_skipped()
    {
        var c = Collection(approval: true);
        var draft = CollectionPost.Create(c, "hello", [], Now);
        Assert.Throws<DomainException>(() => draft.Approve(Now));
        Assert.Throws<DomainException>(() => draft.Reject(Now));

        draft.RequestApproval(Now);
        Assert.Throws<DomainException>(() => draft.RequestApproval(Now));

        draft.Approve(Now);
        Assert.Throws<DomainException>(() => draft.RequestApproval(Now));
        Assert.Throws<DomainException>(() => draft.Approve(Now));
    }

    [Fact]
    public void Only_approved_posts_are_usable_in_a_collection_that_requires_approval()
    {
        var plain = Collection();
        var strict = Collection(approval: true);
        var draft = CollectionPost.Create(strict, "a", [], Now);
        var pending = CollectionPost.Create(strict, "b", [], Now);
        pending.RequestApproval(Now);
        var approved = CollectionPost.Create(strict, "c", [], Now);
        approved.RequestApproval(Now);
        approved.Approve(Now);

        Assert.False(draft.IsUsable(strict));
        Assert.False(pending.IsUsable(strict));
        Assert.True(approved.IsUsable(strict));
        // Approval does not matter while it is off.
        Assert.True(draft.IsUsable(plain));
    }

    [Fact]
    public void Turning_approval_on_leaves_approved_posts_approved_and_off_makes_every_post_usable()
    {
        var c = Collection();
        var p = CollectionPost.Create(c, "hello", [], Now);

        c.Update(c.Name, c.Description, null, new CollectionSettings { RequireApproval = true });
        Assert.Equal(PostApproval.Approved, p.Approval);
        Assert.True(p.IsUsable(c));

        var draft = CollectionPost.Create(c, "x", [], Now);
        Assert.False(draft.IsUsable(c));
        c.Update(c.Name, c.Description, null, new CollectionSettings { RequireApproval = false });
        Assert.True(draft.IsUsable(c));
        Assert.Equal(PostApproval.Draft, draft.Approval); // approves nothing by itself
    }

    [Fact]
    public void Editing_an_approved_post_of_an_approval_collection_sends_it_back_to_draft()
    {
        var c = Collection(approval: true);
        var p = CollectionPost.Create(c, "hello", [], Now);
        p.RequestApproval(Now);
        p.Approve(Now);

        p.Edit("hello", [], c, Now.AddMinutes(1)); // nothing changed
        Assert.Equal(PostApproval.Approved, p.Approval);

        p.Edit("hello again", [], c, Now.AddMinutes(2));
        Assert.Equal(PostApproval.Draft, p.Approval);
        Assert.Equal("hello again", p.Text);
        Assert.Equal(Now.AddMinutes(2), p.UpdatedAt);
    }

    [Fact]
    public void Changing_the_media_counts_as_an_edit()
    {
        var c = Collection(approval: true);
        var p = CollectionPost.Create(c, "hello", [], Now);
        p.RequestApproval(Now);
        p.Approve(Now);

        p.Edit("hello", [Guid.NewGuid()], c, Now);

        Assert.Equal(PostApproval.Draft, p.Approval);
    }

    [Fact]
    public void Editing_a_post_of_a_plain_collection_keeps_it_approved()
    {
        var c = Collection();
        var p = CollectionPost.Create(c, "hello", [], Now);
        p.Edit("changed", [], c, Now);
        Assert.Equal(PostApproval.Approved, p.Approval);
    }

    [Fact]
    public void A_post_that_is_switched_off_is_not_usable_anywhere()
    {
        var c = Collection();
        var p = CollectionPost.Create(c, "hello", [], Now);
        Assert.True(p.Active);
        Assert.True(p.IsUsable(c));

        p.SetActive(false, Now.AddMinutes(1));
        Assert.False(p.IsUsable(c));
        Assert.Equal(Now.AddMinutes(1), p.UpdatedAt);

        p.SetActive(true, Now.AddMinutes(2));
        Assert.True(p.IsUsable(c));
    }

    [Fact]
    public void A_post_of_the_library_is_a_draft_only_when_it_goes_into_an_approval_collection()
    {
        Assert.Equal(PostApproval.Approved, CollectionPost.Create(Ws, "hello", [], requireApproval: false, Now).Approval);
        Assert.Equal(PostApproval.Draft, CollectionPost.Create(Ws, "hello", [], requireApproval: true, Now).Approval);
    }

    [Fact]
    public void Content_changes_send_an_approved_post_back_to_draft_when_any_of_its_collections_asks_for_approval()
    {
        var p = CollectionPost.Create(Ws, "hello", [], requireApproval: true, Now);
        p.RequestApproval(Now);
        p.Approve(Now);

        p.Edit("hello", [], requireApproval: false, Now); // none of its collections asks: stays approved
        p.Edit("hello 2", [], requireApproval: false, Now);
        Assert.Equal(PostApproval.Approved, p.Approval);

        p.Edit("hello 3", [], requireApproval: true, Now);
        Assert.Equal(PostApproval.Draft, p.Approval);
    }

    [Fact]
    public void A_post_has_no_limits_until_it_is_given_some()
    {
        var s = new CollectionPostSettings();
        Assert.True(s.AllowsAt(new DateOnly(2026, 10, 3), 0));
        Assert.True(s.AllowsAt(new DateOnly(2026, 10, 3), 23 * 60 + 59));
    }

    [Fact]
    public void A_post_allows_only_its_weekdays_dates_and_hours()
    {
        var saturday = new DateOnly(2026, 10, 3);
        var weekdays = new CollectionPostSettings { Weekdays = [1, 2, 3, 4, 5] };
        Assert.False(weekdays.AllowsAt(saturday, 600));
        Assert.True(weekdays.AllowsAt(saturday.AddDays(2), 600)); // Monday

        var dates = new CollectionPostSettings { ValidFrom = saturday, ValidUntil = saturday.AddDays(6) };
        Assert.False(dates.AllowsAt(saturday.AddDays(-1), 600));
        Assert.True(dates.AllowsAt(saturday, 600));
        Assert.True(dates.AllowsAt(saturday.AddDays(6), 600));
        Assert.False(dates.AllowsAt(saturday.AddDays(7), 600));

        var evening = new CollectionPostSettings { TimeFrom = "18:00", TimeTo = "21:00" };
        Assert.False(evening.AllowsAt(saturday, 17 * 60 + 59));
        Assert.True(evening.AllowsAt(saturday, 18 * 60));
        Assert.True(evening.AllowsAt(saturday, 21 * 60));
        Assert.False(evening.AllowsAt(saturday, 21 * 60 + 1));

        // A window whose end is before its start runs past midnight.
        var night = new CollectionPostSettings { TimeFrom = "22:00", TimeTo = "02:00" };
        Assert.True(night.AllowsAt(saturday, 23 * 60));
        Assert.True(night.AllowsAt(saturday, 60));
        Assert.False(night.AllowsAt(saturday, 12 * 60));
    }

    [Fact]
    public void A_post_checks_its_own_settings()
    {
        var p = CollectionPost.Create(Ws, "hello", [], requireApproval: false, Now);
        Assert.Throws<DomainException>(() => p.UpdateSettings(new CollectionPostSettings { TimeFrom = "18:00" }, Now));
        Assert.Throws<DomainException>(() => p.UpdateSettings(new CollectionPostSettings { TimeFrom = "25:00", TimeTo = "26:00" }, Now));
        Assert.Throws<DomainException>(() => p.UpdateSettings(new CollectionPostSettings { Weekdays = [7] }, Now));
        Assert.Throws<DomainException>(() => p.UpdateSettings(new CollectionPostSettings { MaxPerDay = 51 }, Now));
        Assert.Throws<DomainException>(() => p.UpdateSettings(
            new CollectionPostSettings { ValidFrom = new DateOnly(2026, 10, 5), ValidUntil = new DateOnly(2026, 10, 4) }, Now));
        Assert.Throws<DomainException>(() => p.UpdateSettings(new CollectionPostSettings { Hashtags = new string('x', 501) }, Now));

        p.UpdateSettings(new CollectionPostSettings { Weekdays = [5, 1, 1], TimeFrom = " 09:00 ", TimeTo = "10:30", MaxPerDay = 2 }, Now.AddMinutes(1));
        Assert.Equal([1, 5], p.Settings.Weekdays);
        Assert.Equal(("09:00", "10:30", 2), (p.Settings.TimeFrom, p.Settings.TimeTo, p.Settings.MaxPerDay));
        Assert.Equal(Now.AddMinutes(1), p.UpdatedAt);
    }

    [Fact]
    public void A_post_writes_with_its_own_hashtags_and_footer_when_it_has_them()
    {
        var c = Collection();
        c.Update(c.Name, c.Description, null, new CollectionSettings { Hashtags = "#ร้าน", Footer = "ติดต่อ 081", FooterPos = FooterPosition.End });
        var p = CollectionPost.Create(c, "hello", [], Now);

        var follow = p.SettingsIn(c);
        Assert.Equal(("#ร้าน", "ติดต่อ 081", FooterPosition.End), (follow.Hashtags, follow.Footer, follow.FooterPos));

        p.UpdateSettings(new CollectionPostSettings { Hashtags = "#โปร", Footer = "", FooterPos = FooterPosition.Top }, Now);
        var own = p.SettingsIn(c);
        Assert.Equal(("#โปร", "", FooterPosition.Top), (own.Hashtags, own.Footer, own.FooterPos));
        Assert.Equal("#ร้าน", c.Settings.Hashtags); // the collection is not touched
    }
}
