using NSubstitute;
using SIRIAUTOPOST.Application.Features.Posts.Commands;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Tests;

public class CreatePostCommandHandlerTests
{
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Adds_the_post_and_saves()
    {
        var handler = new CreatePostCommandHandler(_posts, _uow, _clock);

        var dto = await handler.HandleAsync(new CreatePostCommand("hello", "facebook.com/groups/1", null));

        _posts.Received(1).Add(Arg.Is<Post>(p => p.Content == "hello"));
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("https://www.facebook.com/groups/1/", dto.GroupUrl);
        Assert.Equal("Draft", dto.Status);
    }

    [Fact]
    public async Task Schedules_when_a_time_is_given()
    {
        var handler = new CreatePostCommandHandler(_posts, _uow, _clock);

        var dto = await handler.HandleAsync(
            new CreatePostCommand("hello", "facebook.com/groups/1", _clock.GetUtcNow().AddHours(1)));

        Assert.Equal("Scheduled", dto.Status);
    }
}
