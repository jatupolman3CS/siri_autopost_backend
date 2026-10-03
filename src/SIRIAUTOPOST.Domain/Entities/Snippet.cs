using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// Reusable text (sign-offs, hashtags, promo terms) inserted into posts.
public class Snippet : Entity
{
    public const int MaxTitleLength = 120;
    public const int MaxTextLength = 2000;

    public Guid WorkspaceId { get; private set; }
    public string Title { get; private set; } = "";
    public string Text { get; private set; } = "";
    public int UsedCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Snippet() { } // EF Core

    public static Snippet Create(Guid workspaceId, string title, string text, DateTimeOffset now, int usedCount = 0)
    {
        var t = (title ?? "").Trim();
        var x = (text ?? "").Trim();
        if (t.Length == 0 || x.Length == 0) throw new DomainException("กรุณาใส่ชื่อและข้อความ");
        if (t.Length > MaxTitleLength || x.Length > MaxTextLength) throw new DomainException("ชื่อหรือข้อความยาวเกินไป");
        return new Snippet { WorkspaceId = workspaceId, Title = t, Text = x, CreatedAt = now, UsedCount = usedCount };
    }
}
