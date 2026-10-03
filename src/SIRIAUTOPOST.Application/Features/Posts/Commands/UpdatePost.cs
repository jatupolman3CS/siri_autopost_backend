using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Posts.Commands;

public sealed record UpdatePostCommand(Guid Id, string Content, DateTimeOffset? ScheduledAt) : ICommand<PostDto>;

public sealed class UpdatePostCommandHandler(IPostRepository posts, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<UpdatePostCommand, PostDto>
{
    public async Task<PostDto> HandleAsync(UpdatePostCommand command, CancellationToken ct = default)
    {
        var post = await posts.GetByIdAsync(command.Id, ct) ?? throw new NotFoundException(nameof(Post), command.Id);
        var now = clock.GetUtcNow();
        post.UpdateContent(command.Content, now);
        if (command.ScheduledAt is { } at) post.Schedule(at, now);
        await uow.SaveChangesAsync(ct);
        return PostDto.From(post);
    }
}
