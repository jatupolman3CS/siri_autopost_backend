using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// One browser with the extension, paired to a workspace. It authenticates with a secret key;
// only the key's SHA-256 hash is stored.
public class Device : Entity
{
    public const int MaxNameLength = 80;
    /// <summary>A device counts as online when it called in within this window.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(100);

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    public string Browser { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string KeyHash { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }
    /// <summary>Set in the web app: the device takes no posted jobs until it is turned off again.</summary>
    public bool JobsPaused { get; private set; }

    private Device() { } // EF Core

    public static Device Pair(Guid workspaceId, string name, string browser, string version, string keyHash, DateTimeOffset now)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) n = "เครื่องไม่มีชื่อ";
        return new Device
        {
            WorkspaceId = workspaceId,
            Name = Cut(n, MaxNameLength),
            Browser = Cut((browser ?? "").Trim(), 120),
            Version = Cut((version ?? "").Trim(), 40),
            KeyHash = keyHash,
            CreatedAt = now,
            LastSeenAt = now,
        };
    }

    public bool IsOnline(DateTimeOffset now) => LastSeenAt is { } seen && now - seen <= OnlineWindow;

    public void Seen(string? version, DateTimeOffset now)
    {
        LastSeenAt = now;
        if (!string.IsNullOrWhiteSpace(version)) Version = Cut(version.Trim(), 40);
    }

    /// <summary>Renamed in the web app; an empty name keeps the old one.</summary>
    public void Rename(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length > 0) Name = Cut(n, MaxNameLength);
    }

    public void SetJobsPaused(bool paused) => JobsPaused = paused;

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}

// A short-lived code shown in the web app; the extension trades it for a device key.
public class DevicePairing : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public Guid WorkspaceId { get; private set; }
    public string Code { get; private set; } = "";
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    private DevicePairing() { } // EF Core

    public static DevicePairing Create(Guid workspaceId, string code, DateTimeOffset now) =>
        new() { WorkspaceId = workspaceId, Code = code, ExpiresAt = now + Lifetime };

    public void Use(DateTimeOffset now)
    {
        if (UsedAt is not null) throw new DomainException("รหัสจับคู่นี้ถูกใช้ไปแล้ว");
        if (now > ExpiresAt) throw new DomainException("รหัสจับคู่หมดอายุแล้ว สร้างรหัสใหม่ในหน้าทีมและเวิร์กสเปซ");
        UsedAt = now;
    }
}
