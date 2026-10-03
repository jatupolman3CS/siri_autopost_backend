using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Application.Interfaces;

/// <summary>The signed-in user of the current request.</summary>
public interface ICurrentUser
{
    /// <summary>Throws AuthenticationException when nobody is signed in.</summary>
    Guid UserId { get; }
}

public interface IPasswordHasher
{
    string Hash(User user, string password);
    bool Verify(User user, string hash, string password);
}

public sealed record AuthToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AuthToken Create(User user);
}

/// <summary>Randomness behind the smart delay; swapped for a fixed source in tests.</summary>
public interface IRandomSource
{
    double NextDouble();
}

/// <summary>Fills a new workspace with sample social accounts and snippets so it can be tried right away.</summary>
public interface IWorkspaceSeeder
{
    Task SeedAsync(Workspace workspace, CancellationToken ct = default);
}

/// <summary>The paired extension making the current request (X-Device-Key).</summary>
public interface ICurrentDevice
{
    /// <summary>Throws AuthenticationException when the request has no valid device key.</summary>
    Guid DeviceId { get; }
    Guid WorkspaceId { get; }
}

/// <summary>Secrets for device pairing: random codes and keys, and the hash stored for a key.</summary>
public interface IDeviceSecrets
{
    /// <summary>A short code people can type, like "K7QF-2MXP".</summary>
    string NewPairingCode();
    string NewDeviceKey();
    string Hash(string deviceKey);
}
