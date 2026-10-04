using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class SetLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();
    private static readonly Guid Set = Guid.NewGuid();

    private static SetLink Link(string? url = "https://www.facebook.com/groups/baan.dee", string? name = null, string? code = null, int dailyMax = 0) =>
        SetLink.Create(Ws, Set, name, url, code, dailyMax, Now);

    [Fact]
    public void A_group_address_is_stored_normalised_and_names_the_link()
    {
        var l = Link("m.facebook.com/groups/baan.dee_shop/?ref=x");

        Assert.Equal("https://www.facebook.com/groups/baan.dee_shop", l.Url);
        Assert.Equal("baan dee shop", l.Name);
        Assert.True(l.IsValid);
        Assert.True(l.IsUsable);
        Assert.True(l.Enabled);
        Assert.Equal(LinkHealth.Ok, l.Health);
    }

    [Fact]
    public void A_name_that_was_given_is_kept()
    {
        Assert.Equal("ตลาดนัด", Link(name: "  ตลาดนัด ").Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("https://example.com/not-a-group")]
    public void A_blank_or_invalid_address_makes_an_invalid_row_that_is_never_used(string? url)
    {
        var l = Link(url);

        Assert.False(l.IsValid);
        Assert.False(l.IsUsable);
        Assert.Equal((url ?? "").Trim(), l.Url);
        Assert.Equal("", l.Name);
    }

    [Fact]
    public void Values_are_limited()
    {
        Assert.Throws<DomainException>(() => Link(dailyMax: 51));
        Assert.Throws<DomainException>(() => Link(dailyMax: -1));
        Assert.Throws<DomainException>(() => Link(code: new string('x', 101)));
        Assert.Throws<DomainException>(() => Link(name: new string('x', 201)));
        Assert.Throws<DomainException>(() => Link(url: "https://example.com/" + new string('x', 300)));
        Assert.Equal(50, Link(dailyMax: 50).DailyMax);
        Assert.Equal("ราคาพิเศษ", Link(code: " ราคาพิเศษ ").Code);
    }

    [Fact]
    public void Failures_count_up_and_a_success_resets_them()
    {
        var l = Link();
        l.RecordFailure();
        l.RecordFailure();
        Assert.Equal(2, l.FailStreak);

        l.RecordSuccess();
        Assert.Equal(0, l.FailStreak);
        Assert.Equal(LinkHealth.Ok, l.Health);
    }

    [Fact]
    public void A_group_that_holds_posts_for_approval_goes_pending_until_a_post_succeeds()
    {
        var l = Link();
        l.RecordPendingApproval();
        Assert.Equal(LinkHealth.Pending, l.Health);
        Assert.True(l.Enabled);

        l.RecordSuccess();
        Assert.Equal(LinkHealth.Ok, l.Health);
    }

    [Fact]
    public void Too_many_failures_switch_the_link_off_and_enabling_brings_it_back()
    {
        var l = Link();
        l.AutoDisable(3);

        Assert.False(l.Enabled);
        Assert.Equal(LinkHealth.Off, l.Health);
        Assert.Equal(3, l.FailStreak);
        Assert.False(l.IsUsable);

        l.Enable();
        Assert.True(l.Enabled);
        Assert.Equal(LinkHealth.Ok, l.Health);
        Assert.Equal(0, l.FailStreak);
    }

    [Fact]
    public void Switching_off_by_hand_forgets_the_failures()
    {
        var l = Link();
        l.RecordFailure();
        l.DisableManually();

        Assert.False(l.Enabled);
        Assert.Equal(LinkHealth.Off, l.Health);
        Assert.Equal(0, l.FailStreak);
    }

    [Fact]
    public void A_link_that_is_off_stays_off_when_a_late_result_arrives()
    {
        var l = Link();
        l.DisableManually();

        l.RecordSuccess();
        l.RecordPendingApproval();

        Assert.Equal(LinkHealth.Off, l.Health);
        Assert.False(l.Enabled);
    }

    [Fact]
    public void Edit_changes_the_values_and_the_switch()
    {
        var l = Link();
        l.RecordFailure();

        l.Edit("ชื่อใหม่", "https://fb.com/groups/other", "A1", 5, enabled: false);
        Assert.Equal("ชื่อใหม่", l.Name);
        Assert.Equal("https://www.facebook.com/groups/other", l.Url);
        Assert.Equal("A1", l.Code);
        Assert.Equal(5, l.DailyMax);
        Assert.False(l.Enabled);
        Assert.Equal(LinkHealth.Off, l.Health);

        l.Edit("ชื่อใหม่", l.Url, "A1", 5, enabled: true);
        Assert.True(l.Enabled);
        Assert.Equal(LinkHealth.Ok, l.Health);
    }

    [Fact]
    public void Editing_without_changing_the_switch_keeps_the_health()
    {
        var l = Link();
        l.RecordPendingApproval();

        l.Edit("x", l.Url, "", 0, enabled: true);

        Assert.Equal(LinkHealth.Pending, l.Health);
    }

    [Fact]
    public void A_name_made_from_a_very_long_address_is_cut_to_fit()
    {
        var l = Link("facebook.com/groups/" + new string('a', 250));
        Assert.Equal(200, l.Name.Length);
        Assert.True(SetLink.Fits(l.Url, "x"));
        Assert.False(SetLink.Fits("https://www.facebook.com/groups/" + new string('a', 300), null));
        Assert.False(SetLink.Fits(l.Url, new string('x', 101)));
    }

    [Fact]
    public void An_emptied_name_is_derived_from_the_address_again()
    {
        var l = Link(name: "Custom");
        l.Edit("", "https://www.facebook.com/groups/baan.dee", "", 0, true);
        Assert.Equal("baan dee", l.Name);
    }
}
