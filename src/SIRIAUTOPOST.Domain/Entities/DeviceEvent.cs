namespace SIRIAUTOPOST.Domain.Entities;

// One thing that happened to a device, in the order it happened (Seq). The web app streams these
// events instead of polling: a client that lost its stream asks for everything after the last Seq
// it saw, so nothing is missed. Rows are written in the same transaction as the change they describe
// and pruned per device after a while; the entities they point at stay the source of truth.
public class DeviceEvent
{
    /// <summary>Events kept per device; older ones are deleted.</summary>
    public const int KeepPerDevice = 2000;
    public const int MaxPayloadLength = 400_000;

    /// <summary>Database-assigned, strictly increasing across the platform.</summary>
    public long Seq { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid DeviceId { get; private set; }
    /// <summary>One of <see cref="DeviceEventType"/>.</summary>
    public string Type { get; private set; } = "";
    /// <summary>A JSON object; small, the client fetches the full record when it needs more.</summary>
    public string Payload { get; private set; } = "{}";
    public DateTimeOffset At { get; private set; }

    private DeviceEvent() { } // EF Core

    public static DeviceEvent Create(Guid workspaceId, Guid deviceId, string type, string payloadJson, DateTimeOffset now)
    {
        if (!DeviceEventType.All.Contains(type)) throw new ArgumentOutOfRangeException(nameof(type), type, "unknown device event type");
        var payload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        if (payload.Length > MaxPayloadLength) throw new ArgumentOutOfRangeException(nameof(payloadJson), "payload too large");
        return new DeviceEvent
        {
            WorkspaceId = workspaceId,
            DeviceId = deviceId,
            Type = type,
            Payload = payload,
            At = now,
        };
    }
}

/// <summary>The event names on the stream (the "event:" field of the web app's stream).</summary>
public static class DeviceEventType
{
    /// <summary>A device was paired to the workspace: { name, accountId }.</summary>
    public const string Paired = "device.paired";
    /// <summary>A device was unbound (by the owner's team or the platform admin): { name }.</summary>
    public const string Revoked = "device.revoked";
    /// <summary>A device called in after being offline: { version }.</summary>
    public const string Online = "device.online";
    /// <summary>The extension reported a new run state: { state, at }.</summary>
    public const string State = "device.state";
    /// <summary>New log lines: { lines: [{ t, level, msg }], truncated }. With truncated, fetch the log.</summary>
    public const string Log = "device.log";
    /// <summary>The log was emptied: {}.</summary>
    public const string LogCleared = "device.log_cleared";
    /// <summary>A command changed status: { id, cmd, status, result }.</summary>
    public const string Command = "device.command";
    /// <summary>The settings moved to a new revision: { revision, byDevice }.</summary>
    public const string Config = "device.config";
    /// <summary>A post of the device's account changed: { postId, status, failureCode }.</summary>
    public const string Post = "post";
    /// <summary>The groups of the device's account changed (the extension synced a new list): { count }.</summary>
    public const string Groups = "device.groups";
    /// <summary>The web app renamed the device or paused/resumed its jobs: { name, jobsPaused }.</summary>
    public const string Updated = "device.updated";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Paired, Revoked, Online, State, Log, LogCleared, Command, Config, Post, Groups, Updated,
    };
}
