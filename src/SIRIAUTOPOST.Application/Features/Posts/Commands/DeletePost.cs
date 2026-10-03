using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Posts.Commands;

public sealed record DeletePostCommand(Guid Id) : ICommand<Unit>;

public sealed class DeletePostCommandHandler(IPostRepository posts, IUnitOfWork uow) : ICommandHandler<DeletePostCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeletePostCommand command, CancellationToken ct = default)
    {
        var post = await posts.GetByIdAsync(command.Id, ct) ?? throw new NotFoundException(nameof(Post), command.Id);
        posts.Remove(post);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
