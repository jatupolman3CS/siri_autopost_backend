namespace SIRIAUTOPOST.Domain.Entities;

// A post brought in from an exported profile archive (Facebook "Download your information"). Its media lives in
// object storage (Cloudflare R2); only the addresses are kept here.
public class ImportedPost : Entity
{
    public Guid WorkspaceId { get; private set; }
    public string Source { get; private set; } = "";
    /// <summary>Stable id inside the source, so importing the same archive twice does not duplicate rows.</summary>
    public string SourceKey { get; private set; } = "";
    public DateTimeOffset PostedAt { get; private set; }
    public string? Title { get; private set; }
    public string Text { get; private set; } = "";
    public string? LinkUrl { get; private set; }
    public List<ImportedMedia> Media { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    private ImportedPost() { } // EF Core

    /// <summary>Replaces the media list (a new list, so EF notices the change).</summary>
    public void ReplaceMedia(List<ImportedMedia> media) => Media = media.Select(m => new ImportedMedia { Key = m.Key, Url = m.Url, ContentType = m.ContentType, Size = m.Size, SourcePath = m.SourcePath }).ToList();

    public static ImportedPost Create(
        Guid workspaceId, string source, string sourceKey, DateTimeOffset postedAt, string? title, string text,
        string? linkUrl, IEnumerable<ImportedMedia> media, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId,
            Source = source,
            SourceKey = sourceKey,
            PostedAt = postedAt.ToUniversalTime(),
            Title = title,
            Text = text,
            LinkUrl = linkUrl,
            Media = media.ToList(),
            CreatedAt = now,
        };
}

/// <summary>One file of an imported post, already uploaded: <c>Key</c> is the object key in the bucket.</summary>
public class ImportedMedia
{
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string SourcePath { get; set; } = "";
}
