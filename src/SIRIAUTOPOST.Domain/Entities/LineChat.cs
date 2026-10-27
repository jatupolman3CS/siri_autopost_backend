namespace SIRIAUTOPOST.Domain.Entities;

public class LineChat : Entity
{
    public const int MaxNameLength = 200;
    public const int MaxUserIdLength = 100;
    public const int MaxUrlLength = 300;

    public Guid WorkspaceId { get; private set; }
    public Guid AccountId { get; private set; }
    public string UserId { get; private set; } = "";
    public string ChatUrl { get; private set; } = "";
    public string Name { get; private set; } = "";
    public List<string> Labels { get; private set; } = [];
    public DateTimeOffset? LastMessageAt { get; private set; }
    public bool Unread { get; private set; }
    public bool DoNotSend { get; private set; }
    public DateTimeOffset SyncedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private LineChat() { } // EF Core

    public static LineChat Create(
        Guid workspaceId, Guid accountId, string userId, string chatUrl, string name,
        IEnumerable<string>? labels, DateTimeOffset? lastMessageAt, bool unread, DateTimeOffset now, bool doNotSend = false)
    {
        return new LineChat
        {
            WorkspaceId = workspaceId,
            AccountId = accountId,
            UserId = (userId ?? "").Trim(),
            ChatUrl = (chatUrl ?? "").Trim(),
            Name = CleanName(name),
            Labels = labels?.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList() ?? [],
            LastMessageAt = lastMessageAt,
            Unread = unread,
            DoNotSend = doNotSend,
            SyncedAt = now,
            CreatedAt = now,
        };
    }

    public void Upsert(string name, string chatUrl, IEnumerable<string>? labels, DateTimeOffset? lastMessageAt, bool unread, DateTimeOffset now)
    {
        Name = CleanName(name);
        ChatUrl = (chatUrl ?? "").Trim();
        Labels = labels?.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList() ?? [];
        LastMessageAt = lastMessageAt;
        Unread = unread;
        SyncedAt = now;
    }

    public void SetDoNotSend(bool doNotSend) => DoNotSend = doNotSend;

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) n = "ลูกค้า";
        return n.Length > MaxNameLength ? n[..MaxNameLength] : n;
    }
}
