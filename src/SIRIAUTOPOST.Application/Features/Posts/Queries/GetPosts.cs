using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Posts.Queries;

public sealed record GetPostsQuery : IQuery<IReadOnlyList<PostDto>>;

public sealed class GetPostsQueryHandler(IPostRepository posts) : IQueryHandler<GetPostsQuery, IReadOnlyList<PostDto>>
{
    public async Task<IReadOnlyList<PostDto>> HandleAsync(GetPostsQuery query, CancellationToken ct = default)
    {
        var list = await posts.ListAsync(ct);
        return list.Select(PostDto.From).ToList();
    }
}
