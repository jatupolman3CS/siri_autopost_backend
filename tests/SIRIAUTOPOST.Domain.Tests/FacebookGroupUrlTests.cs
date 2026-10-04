using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Tests;

public class FacebookGroupUrlTests
{
    [Theory]
    [InlineData("https://www.facebook.com/groups/baandee", "https://www.facebook.com/groups/baandee")]
    [InlineData("http://facebook.com/groups/baandee", "https://www.facebook.com/groups/baandee")]
    [InlineData("facebook.com/groups/baandee", "https://www.facebook.com/groups/baandee")]
    [InlineData("https://m.facebook.com/groups/baandee", "https://www.facebook.com/groups/baandee")]
    [InlineData("https://web.facebook.com/groups/baandee/", "https://www.facebook.com/groups/baandee")]
    [InlineData("https://mbasic.facebook.com/groups/baandee?ref=share", "https://www.facebook.com/groups/baandee")]
    [InlineData("https://fb.com/groups/baandee", "https://www.facebook.com/groups/baandee")]
    [InlineData("HTTPS://WWW.FACEBOOK.COM/groups/BaanDee.Shop", "https://www.facebook.com/groups/BaanDee.Shop")]
    [InlineData("  https://www.facebook.com/groups/123456789012345/permalink/42  ", "https://www.facebook.com/groups/123456789012345")]
    [InlineData("https://www.facebook.com/groups/baan_dee-shop", "https://www.facebook.com/groups/baan_dee-shop")]
    public void Every_spelling_of_a_group_becomes_the_same_address(string raw, string expected) =>
        Assert.Equal(expected, FacebookGroupUrl.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://www.facebook.com/baandee")]
    [InlineData("https://www.facebook.com/groups/")]
    [InlineData("https://www.facebook.com/groupsbaandee")]
    [InlineData("https://example.com/groups/baandee")]
    [InlineData("https://notfacebook.com/groups/baandee")]
    [InlineData("baandee")]
    public void Anything_else_is_not_a_group(string? raw)
    {
        Assert.Null(FacebookGroupUrl.Normalize(raw));
        Assert.False(FacebookGroupUrl.IsValid(raw));
    }

    [Fact]
    public void The_slug_keeps_its_case_and_names_the_group()
    {
        Assert.Equal("BaanDee.Shop", FacebookGroupUrl.Slug("fb.com/groups/BaanDee.Shop"));
        Assert.Equal("Baan Dee Shop", FacebookGroupUrl.NameFromSlug("Baan.Dee__Shop"));
        Assert.Equal("123456", FacebookGroupUrl.NameFromSlug("123456"));
    }
}
