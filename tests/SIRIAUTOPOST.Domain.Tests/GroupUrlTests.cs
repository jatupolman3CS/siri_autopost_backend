using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class GroupUrlTests
{
    [Theory]
    [InlineData("https://www.facebook.com/groups/123456")]
    [InlineData("facebook.com/groups/123456/")]
    [InlineData("https://m.facebook.com/groups/123456?ref=share")]
    [InlineData("http://web.facebook.com/groups/123456/posts/9")]
    public void Normalizes_to_canonical_form(string raw) =>
        Assert.Equal("https://www.facebook.com/groups/123456/", GroupUrl.Create(raw).Value);

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/groups/1")]
    [InlineData("https://www.facebook.com/profile/1")]
    public void Rejects_non_group_links(string raw) =>
        Assert.Throws<DomainException>(() => GroupUrl.Create(raw));

    [Fact]
    public void Equal_values_are_equal() =>
        Assert.Equal(GroupUrl.Create("facebook.com/groups/1"), GroupUrl.Create("https://m.facebook.com/groups/1/"));
}
