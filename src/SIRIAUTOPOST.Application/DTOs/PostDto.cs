using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Application.DTOs;

public sealed record PostDto(
    Guid Id,
    string Content,
    string GroupUrl,
    string Status,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? PublishedAt,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PostDto From(Post p) => new(
        p.Id, p.Content, p.GroupUrl.Value, p.Status.ToString(), p.ScheduledAt, p.PublishedAt,
        p.FailureReason, p.CreatedAt, p.UpdatedAt);
}
