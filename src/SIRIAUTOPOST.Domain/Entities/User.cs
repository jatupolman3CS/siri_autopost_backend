using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Entities;

public class User : Entity
{
    public const int MaxNameLength = 120;

    public string Email { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public UserRole Role { get; private set; }
    public PlanKey Plan { get; private set; }
    public BillingCycle Cycle { get; private set; }
    public CustomerStatus Status { get; private set; }
    /// <summary>The Stripe customer behind this user's payments (created on the first checkout).</summary>
    public string? StripeCustomerId { get; private set; }
    /// <summary>The live Stripe subscription that pays for <see cref="Plan"/>; null on Free and on plans the admin granted.</summary>
    public string? StripeSubscriptionId { get; private set; }
    /// <summary>When the current billing period ends (the next renewal, or the end of a cancelled plan).</summary>
    public DateTimeOffset? PlanRenewsAt { get; private set; }
    /// <summary>The subscription ends at <see cref="PlanRenewsAt"/> instead of renewing; the plan stays until then.</summary>
    public bool CancelAtPeriodEnd { get; private set; }
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
            Name = Clip(string.IsNullOrWhiteSpace(name) ? normalized.Split('@')[0] : name.Trim()),
            Role = role,
            Plan = plan,
            Status = CustomerStatus.Active,
            CreatedAt = now,
            LastSeenAt = now,
        };
    }

    private static string Clip(string name) => name.Length <= MaxNameLength ? name : name[..MaxNameLength];

    public bool IsBlocked => Status is CustomerStatus.Suspended or CustomerStatus.Banned;

    /// <summary>Signed-in users that may not post: blocked, or paused by the platform admin.</summary>
    public bool CanPost => !IsBlocked && !Paused;

    public void Seen(DateTimeOffset now) => LastSeenAt = now;

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public bool HasSubscription => StripeSubscriptionId is not null;

    public void LinkStripeCustomer(string customerId) => StripeCustomerId = customerId;

    /// <summary>
    /// Takes over what Stripe says about the subscription that pays for this plan: the plan and cycle, when it
    /// renews, whether it is cancelled at the end of the period, and whether the last payment failed. Blocked
    /// customers stay blocked: only the platform admin lifts that.
    /// </summary>
    public void ApplySubscription(string subscriptionId, PlanKey plan, BillingCycle cycle, DateTimeOffset? renewsAt, bool cancelAtPeriodEnd, bool pastDue)
    {
        StripeSubscriptionId = subscriptionId;
        Plan = plan;
        Cycle = cycle;
        PlanRenewsAt = renewsAt;
        CancelAtPeriodEnd = cancelAtPeriodEnd;
        if (!IsBlocked) Status = pastDue ? CustomerStatus.PastDue : CustomerStatus.Active;
    }

    /// <summary>A payment came in: a past-due customer is current again.</summary>
    public void MarkPaid()
    {
        if (Status == CustomerStatus.PastDue) Status = CustomerStatus.Active;
    }

    /// <summary>A renewal payment failed: the plan stays while Stripe retries.</summary>
    public void MarkPastDue()
    {
        if (Status == CustomerStatus.Active) Status = CustomerStatus.PastDue;
    }

    /// <summary>The subscription is over (cancelled, or unpaid after Stripe's retries): back to Free.</summary>
    public void EndSubscription()
    {
        StripeSubscriptionId = null;
        PlanRenewsAt = null;
        CancelAtPeriodEnd = false;
        Plan = PlanKey.Free;
        if (Status is CustomerStatus.Trial or CustomerStatus.PastDue) Status = CustomerStatus.Active;
    }

    /// <summary>A customer without a subscription (a plan the admin granted) drops to Free by themselves.</summary>
    public void DowngradeToFree()
    {
        if (HasSubscription) throw new DomainException("ยกเลิกการสมัครสมาชิกก่อนจึงจะย้ายไปแผน Free ได้");
        Plan = PlanKey.Free;
    }

    /// <summary>
    /// The platform admin grants another plan without a charge and clears the limit overrides. A customer who
    /// pays through Stripe changes plan themselves: moving the plan here would only be undone by the next event.
    /// </summary>
    public void SetPlanByAdmin(PlanKey plan)
    {
        if (HasSubscription) throw new DomainException("ลูกค้านี้จ่ายผ่าน Stripe อยู่ ให้ลูกค้าเปลี่ยนแผนเอง หรือยกเลิกการสมัครก่อน");
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

    /// <summary>Telegram and LINE notifications need Pro or above.</summary>
    public bool HasNotifications => Plan is PlanKey.Pro or PlanKey.Agency;

    /// <summary>Auto-reply rules need Pro or above.</summary>
    public bool HasAutoReply => Plan is PlanKey.Pro or PlanKey.Agency;

    /// <summary>Shareable client reports are an Agency feature.</summary>
    public bool HasClientReports => Plan is PlanKey.Agency;
}
