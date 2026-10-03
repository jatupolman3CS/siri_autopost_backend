using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Application.Interfaces;

/// <summary>The signed-in user of the current request.</summary>
public interface ICurrentUser
{
    /// <summary>Throws AuthenticationException when nobody is signed in.</summary>
    Guid UserId { get; }

    /// <summary>The platform admin acting as this user (the token's "act" claim), or null.</summary>
    Guid? ImpersonatorId { get; }
}

public interface IPasswordHasher
{
    string Hash(User user, string password);
    bool Verify(User user, string hash, string password);
}

/// <summary>Who Google says the ID token belongs to.</summary>
public sealed record GoogleIdentity(string Email, string? Name);

public interface IGoogleTokenVerifier
{
    /// <summary>Null when Google sign-in is not configured.</summary>
    string? ClientId { get; }
    /// <summary>Checks an ID token from Google Identity Services; throws AuthenticationException when invalid.</summary>
    Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken ct = default);
}

public sealed record AuthToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AuthToken Create(User user);

    /// <summary>A short-lived token for <paramref name="user"/> that names the admin acting as them.</summary>
    AuthToken CreateImpersonation(User user, Guid adminId, TimeSpan lifetime);
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

/// <summary>Durations of recent API requests (recorded by the API's request pipeline).</summary>
public interface IRequestTimings
{
    void Record(TimeSpan duration);
    /// <summary>95th percentile in milliseconds over the recent window, and how many requests it covers.</summary>
    (int? P95Ms, int Samples) Snapshot();
}

/// <summary>The extension the web app hands out for download.</summary>
public interface IExtensionPackage
{
    /// <summary>Its manifest version; null when the server has no copy of the extension.</summary>
    string? Version { get; }
}

public interface IDatabaseProbe
{
    /// <summary>Round trip of a trivial query.</summary>
    Task<TimeSpan> PingAsync(CancellationToken ct = default);
}

/// <summary>
/// Fans out <see cref="Domain.Entities.DeviceEvent"/>s saved by this process to the streams waiting for them
/// (the web app's event stream, a device's long sync). In-process today; the same interface fits Redis
/// pub/sub when the API runs on more than one instance. The database stays the source of truth: a client
/// that missed events reads them back by Seq.
/// </summary>
public interface IDeviceEventBus
{
    /// <summary>Hands saved events (Seq assigned) to every subscriber of their workspace or device.</summary>
    void Publish(IReadOnlyList<Domain.Entities.DeviceEvent> events);

    /// <summary>Events of one workspace from now on. Dispose to stop.</summary>
    IDeviceEventSubscription Subscribe(Guid workspaceId);

    /// <summary>
    /// Waits until an event of <paramref name="type"/> for the device arrives, the timeout passes, the caller
    /// cancels or the application stops. True when the event arrived.
    /// </summary>
    Task<bool> WaitForAsync(Guid deviceId, string type, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Signalled when the application is shutting down: streams end so clients reconnect elsewhere.</summary>
    CancellationToken Stopping { get; }

    DeviceEventBusStats Stats { get; }
}

public interface IDeviceEventSubscription : IDisposable
{
    /// <summary>Completes when the subscription ends (disposed or the application stops).</summary>
    System.Threading.Channels.ChannelReader<Domain.Entities.DeviceEvent> Events { get; }
}

/// <param name="Streams">Web event streams open right now.</param>
/// <param name="DeviceWaits">Devices waiting in a long sync right now.</param>
/// <param name="Published">Events published since the process started.</param>
/// <param name="Dropped">Events a slow stream lost (it refetches by Seq).</param>
public sealed record DeviceEventBusStats(int Streams, int DeviceWaits, long Published, long Dropped);
