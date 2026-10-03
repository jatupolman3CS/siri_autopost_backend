using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Entities;

public class User : Entity
{
    public string Email { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public UserRole Role { get; private set; }
    public PlanKey Plan { get; private set; }
    public BillingCycle Cycle { get; private set; }
    public CustomerStatus Status { get; private set; }
    /// <summary>The platform admin paused this customer's posting (devices take no posts).</summary>
    public bool Paused { get; private set; }
    /// <summary>Platform admin's note about the customer.</summary>
    public string? Note { get; private set; }
    public LimitOverrides Limits { get; private set; } = new();
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSeenAt { get; private set; }

    private User() { } // EF Core

    public static User Create(string email, string name, UserRole role, PlanKey plan, DateTimeOffset now)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length == 0 || !normalized.Contains('@')) throw new DomainException("กรุณาใส่อีเมลที่ถูกต้อง");
        return new User
        {
            Email = normalized,
            Name = string.IsNullOrWhiteSpace(name) ? normalized.Split('@')[0] : name.Trim(),
            Role = role,
            Plan = plan,
            // A paid plan chosen at sign-up starts as a trial until the first payment.
            Status = plan == PlanKey.Free || role == UserRole.Admin ? CustomerStatus.Active : CustomerStatus.Trial,
            CreatedAt = now,
            LastSeenAt = now,
        };
    }

    public bool IsBlocked => Status is CustomerStatus.Suspended or CustomerStatus.Banned;

    /// <summary>Signed-in users that may not post: blocked, or paused by the platform admin.</summary>
    public bool CanPost => !IsBlocked && !Paused;

    public void Seen(DateTimeOffset now) => LastSeenAt = now;

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    /// <summary>Self-service change; a paid charge ends a trial or past-due state.</summary>
    public void ChangePlan(PlanKey plan, BillingCycle cycle, bool paid)
    {
        Plan = plan;
        Cycle = cycle;
        if (paid || plan == PlanKey.Free) Status = Status is CustomerStatus.Trial or CustomerStatus.PastDue ? CustomerStatus.Active : Status;
    }

    /// <summary>The platform admin moves the customer to another plan (no charge).</summary>
    public void SetPlanByAdmin(PlanKey plan)
    {
        Plan = plan;
        Limits = new LimitOverrides();
    }

    public void SetStatus(CustomerStatus status)
    {
        if (Role == UserRole.Admin && status is CustomerStatus.Suspended or CustomerStatus.Banned)
            throw new DomainException("ระงับบัญชีผู้ดูแลแพลตฟอร์มไม่ได้");
        Status = status;
        if (status == CustomerStatus.Active) Paused = false;
        if (status is CustomerStatus.Suspended or CustomerStatus.Banned) Paused = true;
    }

    public void SetPaused(bool paused) => Paused = paused;

    public void SetNote(string? note) => Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];

    public void SetLimits(LimitOverrides limits)
    {
        if (new[] { limits.Accounts, limits.Posts, limits.Devices, limits.Seats }.Any(v => v is < 0))
            throw new DomainException("ขีดจำกัดต้องไม่ติดลบ");
        Limits = limits;
    }

    /// <summary>Advanced anti-ban needs Pro or above.</summary>
    public bool HasAdvancedAntiBan => Plan is PlanKey.Pro or PlanKey.Agency;
}
