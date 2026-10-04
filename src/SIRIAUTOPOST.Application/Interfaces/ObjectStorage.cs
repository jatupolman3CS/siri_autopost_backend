namespace SIRIAUTOPOST.Application.Interfaces;

/// <summary>Private object storage (Cloudflare R2) that holds library files kept outside the database.</summary>
public interface IObjectStorage
{
    /// <summary>False until the endpoint, bucket and keys are configured.</summary>
    bool Enabled { get; }

    /// <summary>Opens an object for reading; null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
}
