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
        Assert.Equal(c.Id, p.CollectionId);
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
    public void Moving_a_post_into_an_approval_collection_makes_it_a_draft_there()
    {
        var plain = Collection();
        var strict = Collection(approval: true, name: "ตรวจก่อนโพสต์");
        var p = CollectionPost.Create(plain, "hello", [], Now);

        p.Move(strict, Now);
        Assert.Equal(strict.Id, p.CollectionId);
        Assert.Equal(PostApproval.Draft, p.Approval);

        p.Move(plain, Now);
        Assert.Equal(plain.Id, p.CollectionId);
    }

    [Fact]
    public void A_post_cannot_move_to_another_workspace()
    {
        var other = PostCollection.Create(Guid.NewGuid(), "x", null, Now);
        var p = CollectionPost.Create(Collection(), "hello", [], Now);
        Assert.Throws<DomainException>(() => p.Move(other, Now));
    }
}
