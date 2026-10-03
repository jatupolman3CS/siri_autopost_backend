using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

public class User : Entity
{
    public string Email { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public UserRole Role { get; private set; }
    public PlanKey Plan { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

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
            CreatedAt = now,
        };
    }

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void ChangePlan(PlanKey plan) => Plan = plan;

    /// <summary>Advanced anti-ban needs Pro or above.</summary>
    public bool HasAdvancedAntiBan => Plan is PlanKey.Pro or PlanKey.Agency;
}
