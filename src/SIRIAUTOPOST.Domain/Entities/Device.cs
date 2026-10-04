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
    /// <summary>The engine paused the device itself (Facebook blocked it, or too many posts failed) until this time.</summary>
    public DateTimeOffset? AutoPausedUntil { get; private set; }
    public string? AutoPauseReason { get; private set; }

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

    /// <summary>Records a call from the device. Returns true when it was offline until now.</summary>
    public bool Seen(string? version, DateTimeOffset now)
    {
        var wasOffline = !IsOnline(now);
        LastSeenAt = now;
        if (!string.IsNullOrWhiteSpace(version)) Version = Cut(version.Trim(), 40);
        return wasOffline;
    }

    /// <summary>Renamed in the web app; an empty name keeps the old one.</summary>
    public void Rename(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length > 0) Name = Cut(n, MaxNameLength);
    }

    public void SetJobsPaused(bool paused) => JobsPaused = paused;

    /// <summary>The engine's own pause is in force: the device is told to take no jobs until it ends.</summary>
    public bool IsAutoPaused(DateTimeOffset now) => AutoPausedUntil is { } until && until > now;

    /// <summary>
    /// Pauses the device until <paramref name="until"/>. A pause that is already longer stays. Returns true when
    /// the device was not auto-paused before (so an event is due).
    /// </summary>
    public bool AutoPause(DateTimeOffset until, string reason, DateTimeOffset now)
    {
        if (until <= now) throw new DomainException("เวลาสิ้นสุดการพักต้องอยู่ในอนาคต");
        var wasPaused = IsAutoPaused(now);
        if (wasPaused && AutoPausedUntil >= until) return false;
        AutoPausedUntil = until;
        AutoPauseReason = Cut(reason.Trim(), 200);
        return !wasPaused;
    }

    /// <summary>The pause is over (or the owner lifted it): clears it. Returns true when there was one to clear.</summary>
    public bool EndAutoPause(DateTimeOffset now, bool force = false)
    {
        if (AutoPausedUntil is not { } until) return false;
        if (!force && until > now) return false;
        AutoPausedUntil = null;
        AutoPauseReason = null;
        return true;
    }

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
