using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// A folder of the media library. Folders are one level deep; a file is in one folder or in none.
public class MediaFolder : Entity
{
    public const int MaxNameLength = 80;
    /// <summary>Folders per workspace.</summary>
    public const int MaxPerWorkspace = 200;

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    private MediaFolder() { } // EF Core

    public static MediaFolder Create(Guid workspaceId, string? name, DateTimeOffset now)
    {
        var f = new MediaFolder { WorkspaceId = workspaceId, CreatedAt = now };
        f.Rename(name);
        return f;
    }

    public void Rename(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new DomainException("กรุณาใส่ชื่อโฟลเดอร์");
        if (n.Length > MaxNameLength) throw new DomainException($"ชื่อโฟลเดอร์ยาวเกิน {MaxNameLength} ตัวอักษร");
        Name = n;
    }
}
