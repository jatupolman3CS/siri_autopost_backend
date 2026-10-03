using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Interfaces;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Features.Posts.Commands;

public sealed record CreatePostCommand(string Content, string GroupUrl, DateTimeOffset? ScheduledAt) : ICommand<PostDto>;

public sealed class CreatePostCommandHandler(IPostRepository posts, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<CreatePostCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(CreatePostCommand command, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var post = Post.Create(command.Content, GroupUrl.Create(command.GroupUrl), now);
        if (command.ScheduledAt is { } at) post.Schedule(at, now);
        posts.Add(post);
        await uow.SaveChangesAsync(ct);
        return PostDto.From(post);
    }
}
