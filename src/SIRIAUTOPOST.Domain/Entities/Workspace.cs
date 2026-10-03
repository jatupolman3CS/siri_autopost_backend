using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Entities;

// A user's space for one brand or client: accounts, posts, library and engine settings.
public class Workspace : Entity
{
    public const int MaxNameLength = 120;

    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public AntiBanSettings AntiBan { get; private set; } = new();
    public OfflineSettings Offline { get; private set; } = new();
    /// <summary>False while the offline simulation holds the extension offline. Real presence comes from devices.</summary>
    public bool ExtensionOnline { get; private set; } = true;

    private Workspace() { } // EF Core

    public static Workspace Create(Guid ownerId, string name, DateTimeOffset now)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new DomainException("กรุณาใส่ชื่อเวิร์กสเปซ");
        if (n.Length > MaxNameLength) throw new DomainException($"ชื่อเวิร์กสเปซยาวเกิน {MaxNameLength} ตัวอักษร");
        return new Workspace { OwnerId = ownerId, Name = n, CreatedAt = now };
    }

    public void UpdateAntiBan(AntiBanSettings settings, bool advancedAllowed)
    {
        settings.Validate();
        if (!advancedAllowed)
        {
            // Human-like behaviour is a Pro feature: keep what the workspace already has.
            settings.Typing = AntiBan.Typing;
            settings.Scroll = AntiBan.Scroll;
            settings.Shuffle = AntiBan.Shuffle;
            settings.AutoPause = AntiBan.AutoPause;
            settings.Warmup = AntiBan.Warmup;
        }
        AntiBan = settings;
    }

    public void UpdateOffline(OfflineSettings settings)
    {
        settings.Validate();
        Offline = settings;
    }

    public void SetExtensionOnline(bool online) => ExtensionOnline = online;
}
