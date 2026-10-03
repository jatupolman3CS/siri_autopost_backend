using System.Text.RegularExpressions;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// The extension's own setup (campaigns, groups, posts, timing, Telegram: the "settings" JSON of
// client/lib/shared.js), kept per paired device so the web app can show and edit it. The device
// pushes local edits and pulls the web's; Revision tells the two sides apart.
public class ExtensionConfig : Entity
{
    /// <summary>Characters of settings JSON (images are stored apart, so this is text only).</summary>
    public const int MaxSettingsLength = 20_000_000;

    public Guid DeviceId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    /// <summary>The settings JSON exactly as last saved; "" until the first save.</summary>
    public string Settings { get; private set; } = "";
    /// <summary>0 = never saved. Goes up by one on every save that changes the settings.</summary>
    public int Revision { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    /// <summary>Holds real data (groups, posts or media), worked out from the JSON on every save.</summary>
    public bool HasContent { get; private set; }
    /// <summary>The last change came from the extension (false: from the web app).</summary>
    public bool UpdatedByDevice { get; private set; }

    private ExtensionConfig() { } // EF Core

    public static ExtensionConfig Create(Guid workspaceId, Guid deviceId) =>
        new() { WorkspaceId = workspaceId, DeviceId = deviceId };

    /// <summary>
    /// Saves new settings. <paramref name="baseRevision"/> is the revision the caller edited (null overwrites
    /// whatever is there); when the settings moved on since, nothing is saved (409). The same JSON again keeps
    /// the revision, so the device does not reload for nothing. Returns true when something changed.
    /// </summary>
    public bool Save(string json, bool hasContent, int? baseRevision, bool byDevice, DateTimeOffset now)
    {
        if (baseRevision is { } b && b != Revision)
            throw new ConflictException($"การตั้งค่าถูกแก้ไปก่อนแล้ว (ฉบับที่ {Revision}) โหลดใหม่แล้วแก้อีกครั้ง");
        if (json.Length > MaxSettingsLength) throw new DomainException("การตั้งค่าใหญ่เกินไป");
        if (json == Settings) return false;
        Settings = json;
        HasContent = hasContent;
        Revision++;
        UpdatedAt = now;
        UpdatedByDevice = byDevice;
        return true;
    }
}

// A photo or video of the extension's posts, shared by every device of a workspace. The id is the
// extension's own (storage key "img:<id>"); an id never changes content, so it is never overwritten.
public partial class ExtensionImage : Entity
{
    /// <summary>The extension accepts videos up to 200 MB.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    public Guid WorkspaceId { get; private set; }
    public string ImageId { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long Size { get; private set; }
    public byte[] Data { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    private ExtensionImage() { } // EF Core

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex IdRegex();

    public static bool ValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    public static ExtensionImage Create(Guid workspaceId, string imageId, string name, string contentType, byte[] data, DateTimeOffset now)
    {
        if (!ValidId(imageId)) throw new DomainException("รหัสรูปไม่ถูกต้อง");
        var type = (contentType ?? "").Trim().ToLowerInvariant();
        if (!type.StartsWith("image/") && !type.StartsWith("video/")) throw new DomainException("รองรับเฉพาะไฟล์รูปภาพและวิดีโอ");
        if (data.Length == 0) throw new DomainException("ไฟล์ว่างเปล่า");
        if (data.LongLength > MaxBytes) throw new DomainException("ไฟล์ใหญ่เกิน 200 MB");
        var n = (name ?? "").Trim();
        return new ExtensionImage
        {
            WorkspaceId = workspaceId,
            ImageId = imageId,
            Name = n.Length == 0 ? "image.jpg" : n.Length > 200 ? n[..200] : n,
            ContentType = type.Length > 100 ? type[..100] : type,
            Size = data.LongLength,
            Data = data,
            CreatedAt = now,
        };
    }
}

// What the extension last reported about its run (the "state" storage key: running, campaign rounds,
// next times, today's count, pauses). Kept apart from the device row, which every device call loads.
public class DeviceState : Entity
{
    public Guid DeviceId { get; private set; }
    public string Json { get; private set; } = "{}";
    public DateTimeOffset UpdatedAt { get; private set; }

    private DeviceState() { } // EF Core

    public static DeviceState Create(Guid deviceId) => new() { DeviceId = deviceId };

    public void Set(string json, DateTimeOffset now)
    {
        Json = json;
        UpdatedAt = now;
    }
}

// One line of the extension's activity log ("บันทึกการทำงาน").
public class DeviceLog : Entity
{
    public const int MaxMessageLength = 2000;
    /// <summary>Lines kept per device; older ones are deleted.</summary>
    public const int KeepPerDevice = 1000;

    public Guid DeviceId { get; private set; }
    /// <summary>The extension's timestamp (Unix milliseconds), also the order of the lines.</summary>
    public long T { get; private set; }
    public string Level { get; private set; } = "info";
    public string Message { get; private set; } = "";

    private DeviceLog() { } // EF Core

    public static DeviceLog Create(Guid deviceId, long t, string? level, string? message)
    {
        var lv = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim();
        var msg = message ?? "";
        return new DeviceLog
        {
            DeviceId = deviceId,
            T = t,
            Level = lv.Length > 20 ? lv[..20] : lv,
            Message = msg.Length > MaxMessageLength ? msg[..MaxMessageLength] : msg,
        };
    }
}

// A button pressed in the web app (start, stop, test post...) for one device. The device takes pending
// commands on its next sync, runs them, and reports the result. A command nobody took within
// Lifetime expires instead of running late (a "start" from yesterday must not run now).
public class DeviceCommand : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The commands the extension runs from the web (remoteCommands in client/background.js).</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>
    {
        "start", "stop", "runNow", "testPost", "tgTest", "tgFindChats", "clearLogs", "syncNow",
    };

    public Guid WorkspaceId { get; private set; }
    public Guid DeviceId { get; private set; }
    public string Cmd { get; private set; } = "";
    public string Args { get; private set; } = "{}";
    public CommandStatus Status { get; private set; }
    public string? Result { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? DoneAt { get; private set; }

    private DeviceCommand() { } // EF Core

    public static DeviceCommand Create(Guid workspaceId, Guid deviceId, string cmd, string argsJson, DateTimeOffset now)
    {
        if (!Allowed.Contains(cmd)) throw new DomainException($"ไม่รู้จักคำสั่ง {cmd}");
        return new DeviceCommand
        {
            WorkspaceId = workspaceId,
            DeviceId = deviceId,
            Cmd = cmd,
            Args = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson,
            Status = CommandStatus.Pending,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Still open: waiting for the device, or handed to it without a result yet. A sync hands out every open
    /// command again (the call that carried it may have been cut short, and the device remembers the ids it ran),
    /// so a command is only given up on after <see cref="Lifetime"/>.
    /// </summary>
    public bool IsOpen => Status is CommandStatus.Pending or CommandStatus.Sent;

    public bool Stale(DateTimeOffset now) => IsOpen && now - CreatedAt > Lifetime;

    public void Expire() => Status = CommandStatus.Expired;

    /// <summary>First delivery to the device; handing the same command out again leaves it as it was.</summary>
    public bool Send(DateTimeOffset now)
    {
        if (Status != CommandStatus.Pending) return false;
        Status = CommandStatus.Sent;
        SentAt = now;
        return true;
    }

    public void Complete(string resultJson, DateTimeOffset now)
    {
        Status = CommandStatus.Done;
        Result = resultJson;
        DoneAt = now;
    }
}
