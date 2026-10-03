using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class PostTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly GroupUrl Group = GroupUrl.Create("facebook.com/groups/123");

    [Fact]
    public void Create_trims_content_and_starts_as_draft()
    {
        var post = Post.Create("  hello  ", Group, Now);

        Assert.Equal("hello", post.Content);
        Assert.Equal(PostStatus.Draft, post.Status);
        Assert.Equal(Now, post.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_content(string content) =>
        Assert.Throws<DomainException>(() => Post.Create(content, Group, Now));

    [Fact]
    public void Schedule_requires_a_future_time()
    {
        var post = Post.Create("hello", Group, Now);

        Assert.Throws<DomainException>(() => post.Schedule(Now, Now));
    }

    [Fact]
    public void Schedule_stores_utc_and_sets_status()
    {
        var post = Post.Create("hello", Group, Now);
        var at = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.FromHours(7));

        post.Schedule(at, Now);

        Assert.Equal(PostStatus.Scheduled, post.Status);
        Assert.Equal(TimeSpan.Zero, post.ScheduledAt!.Value.Offset);
        Assert.Equal(at, post.ScheduledAt);
    }

    [Fact]
    public void Published_post_cannot_be_edited()
    {
        var post = Post.Create("hello", Group, Now);
        post.MarkPublished(Now);

        Assert.Throws<DomainException>(() => post.UpdateContent("changed", Now));
    }
}
