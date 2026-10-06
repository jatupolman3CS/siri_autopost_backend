using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

/// <summary>Maximum posts per day for each platform (Facebook only for now; stored workspaces may still hold the old keys of other networks, which are ignored).</summary>
public class PlatformLimits
{
    public int Fb { get; set; } = 40;

    public int For(Platform p) => Fb;
}

/// <summary>
/// The numbers of the advanced anti-ban page (a Pro feature). Lower plans keep what the workspace has.
/// MinGap, DailyAll, StopFailPct, AutoOffFails, FailStreak, RecentAvoid and Cooldown are enforced by the posting
/// engine; BlockMin/BlockMax set how long a device pauses after Facebook blocks it; Focus is stored only.
/// </summary>
public class AdvancedAntiBanSettings
{
    /// <summary>Minimum minutes between two posts of one account (the larger of this and the smart delay applies).</summary>
    public int MinGap { get; set; } = 2;
    /// <summary>Posts per 24 hours over all platforms; 0 = no cap.</summary>
    public int DailyAll { get; set; }
    /// <summary>A Facebook block pauses the device for a random time between BlockMin and BlockMax hours.</summary>
    public int BlockMin { get; set; } = 24;
    public int BlockMax { get; set; } = 48;
    /// <summary>Consecutive failed posts of an account that pause its device for 2-4 hours; 0 = off.</summary>
    public int FailStreak { get; set; } = 4;
    /// <summary>A link does not get one of the last N posts it already had; 0 = off.</summary>
    public int RecentAvoid { get; set; } = 10;
    /// <summary>Hours before the same link is posted to again; 0 = off.</summary>
    public int Cooldown { get; set; }
    /// <summary>Stored only: the extension does not use a focus window yet.</summary>
    public bool Focus { get; set; } = true;
    /// <summary>Consecutive failures that switch a link off; 0 = never.</summary>
    public int AutoOffFails { get; set; } = 3;
    /// <summary>Stop the engine when more than this percent of the last 24 hours' finished posts failed; 0 = off.</summary>
    public int StopFailPct { get; set; } = 30;

    public const int MaxHours = 168;

    public AdvancedAntiBanSettings Clone() => (AdvancedAntiBanSettings)MemberwiseClone();

    public void Validate()
    {
        if (MinGap is < 0 or > 60) throw new DomainException("ระยะห่างขั้นต่ำต้องอยู่ระหว่าง 0–60 นาที");
        if (DailyAll is < 0 or > 500) throw new DomainException("เพดานรวมต่อวันต้องอยู่ระหว่าง 0–500 โพสต์");
        if (BlockMin is < 1 or > MaxHours || BlockMax is < 1 or > MaxHours)
            throw new DomainException($"เวลาพักเมื่อถูกบล็อกต้องอยู่ระหว่าง 1–{MaxHours} ชั่วโมง");
        if (BlockMax < BlockMin) throw new DomainException("เวลาพักสูงสุดต้องไม่น้อยกว่าเวลาพักต่ำสุด");
        if (FailStreak is < 0 or > 20) throw new DomainException("จำนวนโพสต์ล้มเหลวติดกันต้องอยู่ระหว่าง 0–20");
        if (RecentAvoid is < 0 or > 50) throw new DomainException("จำนวนโพสต์ล่าสุดที่ไม่ซ้ำต้องอยู่ระหว่าง 0–50");
        if (Cooldown is < 0 or > MaxHours) throw new DomainException($"เวลาพักต่อกลุ่มต้องอยู่ระหว่าง 0–{MaxHours} ชั่วโมง");
        if (AutoOffFails is < 0 or > 20) throw new DomainException("จำนวนครั้งที่ล้มเหลวก่อนปิดกลุ่มต้องอยู่ระหว่าง 0–20");
        if (StopFailPct is < 0 or > 100) throw new DomainException("เปอร์เซ็นต์ล้มเหลวที่หยุดระบบต้องอยู่ระหว่าง 0–100");
    }
}

/// <summary>Smart delay, daily limits and human-like behaviour of the posting engine.</summary>
public class AntiBanSettings
{
    public const int MinDelayFloor = 1;
    public const int MaxDelayCeiling = 60;
    /// <summary>The highest posts-per-24-hours a platform may be set to (as high as the overall cap, <see cref="AdvancedAntiBanSettings.DailyAll"/>).</summary>
    public const int MaxDailyLimit = 500;

    public int Min { get; set; } = 3;
    public int Max { get; set; } = 12;
    public PlatformLimits Limits { get; set; } = new();
    public bool Typing { get; set; } = true;
    public bool Scroll { get; set; } = true;
    public bool Shuffle { get; set; } = true;
    public bool AutoPause { get; set; } = true;
    public bool Warmup { get; set; }
    /// <summary>How fast the extension types a post: slow, normal or fast (a Pro setting like the other human-behaviour ones).</summary>
    public string TypingSpeed { get; set; } = "normal";
    public static readonly string[] TypingSpeeds = ["slow", "normal", "fast"];
    /// <summary>The advanced numbers (Pro and above); see <see cref="AdvancedAntiBanSettings"/>.</summary>
    public AdvancedAntiBanSettings Advanced { get; set; } = new();

    public void Validate()
    {
        if (Min < MinDelayFloor || Max > MaxDelayCeiling || Min >= Max)
            throw new DomainException($"ช่วงหน่วงเวลาต้องอยู่ระหว่าง {MinDelayFloor}–{MaxDelayCeiling} นาที และค่าต่ำสุดต้องน้อยกว่าค่าสูงสุด");
        if (Limits.Fb is < 1 or > MaxDailyLimit)
            throw new DomainException($"เพดานต่อวันต้องอยู่ระหว่าง 1–{MaxDailyLimit}");
        if (!TypingSpeeds.Contains(TypingSpeed)) throw new DomainException("ความเร็วในการพิมพ์ต้องเป็น ช้า ปกติ หรือเร็ว");
        Advanced.Validate();
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

    /// <summary>Under the skip policy a post may still go out this late (clock drift, a slow device).</summary>
    public static readonly TimeSpan SkipGrace = TimeSpan.FromMinutes(10);

    /// <summary>How late a post may go out before it is skipped instead.</summary>
    public TimeSpan MaxLateness => Policy == OfflinePolicy.Skip
        ? SkipGrace
        : Window switch
        {
            "30m" => TimeSpan.FromMinutes(30),
            "day" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(2),
        };
}
