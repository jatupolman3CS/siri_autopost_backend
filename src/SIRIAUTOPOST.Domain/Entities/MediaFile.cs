using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// An image or video in the workspace library. The bytes live in the database like the legacy server.
public class MediaFile : Entity
{
    public const long MaxBytes = 100L * 1024 * 1024;

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public MediaKind Kind { get; private set; }
    public long Size { get; private set; }
    public byte[] Data { get; private set; } = [];
    /// <summary>Set when the file lives in object storage (Data is then empty) and is served from this address.</summary>
    public string? ExternalUrl { get; private set; }
    public int UsedCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private MediaFile() { } // EF Core

    /// <summary>A library entry for a file that already sits in object storage; only its address is kept.</summary>
    public static MediaFile CreateExternal(Guid workspaceId, string name, string contentType, long size, string url, DateTimeOffset now)
    {
        var type = (contentType ?? "").Trim().ToLowerInvariant();
        var kind = type.StartsWith("image/") ? MediaKind.Image
            : type.StartsWith("video/") ? MediaKind.Video
            : throw new DomainException("รองรับเฉพาะไฟล์รูปภาพและวิดีโอ");
        var n = Path.GetFileNameWithoutExtension((name ?? "").Trim());
        return new MediaFile
        {
            WorkspaceId = workspaceId,
            Name = string.IsNullOrEmpty(n) ? "file" : n.Length > 200 ? n[..200] : n,
            ContentType = type,
            Kind = kind,
            Size = size,
            ExternalUrl = url,
            CreatedAt = now,
        };
    }

    /// <summary>Points the file at a new address (the storage's public address changed).</summary>
    public void MoveTo(string url) => ExternalUrl = url;

    public static MediaFile Create(Guid workspaceId, string name, string contentType, byte[] data, DateTimeOffset now)
    {
        var type = (contentType ?? "").Trim().ToLowerInvariant();
        var kind = type.StartsWith("image/") ? MediaKind.Image
            : type.StartsWith("video/") ? MediaKind.Video
            : throw new DomainException("รองรับเฉพาะไฟล์รูปภาพและวิดีโอ");
        if (data.Length == 0) throw new DomainException("ไฟล์ว่างเปล่า");
        if (data.LongLength > MaxBytes) throw new DomainException("ไฟล์ใหญ่เกิน 100 MB");
        var n = Path.GetFileNameWithoutExtension((name ?? "").Trim());
        return new MediaFile
        {
            WorkspaceId = workspaceId,
            Name = string.IsNullOrEmpty(n) ? "file" : n.Length > 200 ? n[..200] : n,
            ContentType = type,
            Kind = kind,
            Size = data.LongLength,
            Data = data,
            CreatedAt = now,
        };
    }
}
