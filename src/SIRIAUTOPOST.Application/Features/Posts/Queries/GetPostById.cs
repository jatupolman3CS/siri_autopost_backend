using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Posts.Queries;

public sealed record GetPostByIdQuery(Guid Id) : IQuery<PostDto>;

public sealed class GetPostByIdQueryHandler(IPostRepository posts) : IQueryHandler<GetPostByIdQuery, PostDto>
{
    public async Task<PostDto> HandleAsync(GetPostByIdQuery query, CancellationToken ct = default)
    {
        var post = await posts.GetByIdAsync(query.Id, ct) ?? throw new NotFoundException(nameof(Post), query.Id);
        return PostDto.From(post);
    }
}
