using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

/// <summary>Maximum posts per day for each platform.</summary>
public class PlatformLimits
{
    public int Fb { get; set; } = 40;
    public int X { get; set; } = 20;
    public int Ig { get; set; } = 10;
    public int Tt { get; set; } = 5;
    public int Line { get; set; } = 3;
    public int Th { get; set; } = 10;

    public int For(Platform p) => p switch
    {
        Platform.Fb => Fb,
        Platform.X => X,
        Platform.Ig => Ig,
        Platform.Tt => Tt,
        Platform.Line => Line,
        _ => Th,
    };
}

/// <summary>Smart delay, daily limits and human-like behaviour of the posting engine.</summary>
public class AntiBanSettings
{
    public const int MinDelayFloor = 1;
    public const int MaxDelayCeiling = 60;
    public const int MaxDailyLimit = 200;

    public int Min { get; set; } = 3;
    public int Max { get; set; } = 12;
    public PlatformLimits Limits { get; set; } = new();
    public bool Typing { get; set; } = true;
    public bool Scroll { get; set; } = true;
    public bool Shuffle { get; set; } = true;
    public bool AutoPause { get; set; } = true;
    public bool Warmup { get; set; }

    public void Validate()
    {
        if (Min < MinDelayFloor || Max > MaxDelayCeiling || Min >= Max)
            throw new DomainException($"ช่วงหน่วงเวลาต้องอยู่ระหว่าง {MinDelayFloor}–{MaxDelayCeiling} นาที และค่าต่ำสุดต้องน้อยกว่าค่าสูงสุด");
        foreach (var p in Enum.GetValues<Platform>())
            if (Limits.For(p) is < 1 or > MaxDailyLimit)
                throw new DomainException($"เพดานต่อวันต้องอยู่ระหว่าง 1–{MaxDailyLimit}");
    }
}

/// <summary>What happens to posts that come due while the extension is offline.</summary>
public class OfflineSettings
{
    public static readonly string[] Windows = ["30m", "2h", "day"];

    public OfflinePolicy Policy { get; set; } = OfflinePolicy.Queue;
    public string Window { get; set; } = "2h";
    public bool Line { get; set; } = true;
    public bool Email { get; set; } = true;
    public bool Push { get; set; }

    public void Validate()
    {
        if (!Windows.Contains(Window)) throw new DomainException("กรอบเวลาไม่ถูกต้อง");
    }
}
