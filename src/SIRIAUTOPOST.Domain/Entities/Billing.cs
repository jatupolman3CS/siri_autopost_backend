using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// Price and limits of one plan, edited by the platform admin. null limit = unlimited.
public class PlanSetting
{
    public PlanKey Key { get; private set; }
    /// <summary>Baht per month (monthly billing).</summary>
    public int Price { get; private set; }
    public int? Accounts { get; private set; }
    /// <summary>Posts per 24 hours, all workspaces of the owner together.</summary>
    public int? Posts { get; private set; }
    public int? Devices { get; private set; }
    /// <summary>People in a workspace, owner included.</summary>
    public int? Seats { get; private set; }

    private PlanSetting() { } // EF Core

    public static PlanSetting Create(PlanKey key, int price, int? accounts, int? posts, int? devices, int? seats) =>
        new() { Key = key, Price = price, Accounts = accounts, Posts = posts, Devices = devices, Seats = seats };

    /// <summary>The design's pricing page.</summary>
    public static IReadOnlyList<PlanSetting> Defaults =>
    [
        Create(PlanKey.Free, 0, 1, 10, 1, 1),
        Create(PlanKey.Basic, 290, 2, 30, 1, 1),
        Create(PlanKey.Pro, 790, 10, null, 3, 1),
        Create(PlanKey.Agency, 1990, null, null, null, 10),
    ];

    public void Update(int price, int? accounts, int? posts, int? devices, int? seats)
    {
        if (price < 0 || price > 1_000_000) throw new DomainException("ราคาไม่ถูกต้อง");
        if (Key == PlanKey.Free && price != 0) throw new DomainException("แผน Free ต้องไม่มีค่าใช้จ่าย");
        if (new[] { accounts, posts, devices, seats }.Any(v => v is < 1)) throw new DomainException("ขีดจำกัดต้องมากกว่า 0 หรือเว้นว่างเพื่อไม่จำกัด");
        Price = price;
        Accounts = accounts;
        Posts = posts;
        Devices = devices;
        Seats = seats;
    }
}

// One money movement of a customer. Without a payment provider these are recorded, not charged.
public class Transaction : Entity
{
    public Guid UserId { get; private set; }
    public TransactionType Type { get; private set; }
    /// <summary>Baht, always positive (a refund is money going back).</summary>
    public int Amount { get; private set; }
    public PlanKey Plan { get; private set; }
    public BillingCycle Cycle { get; private set; }
    public string? PromoCode { get; private set; }
    /// <summary>The charge a refund gives back.</summary>
    public Guid? RefundOfId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Transaction() { } // EF Core

    public static Transaction Charge(Guid userId, int amount, PlanKey plan, BillingCycle cycle, string? promo, DateTimeOffset now) =>
        new() { UserId = userId, Type = TransactionType.Charge, Amount = amount, Plan = plan, Cycle = cycle, PromoCode = promo, CreatedAt = now };

    public Transaction RefundOf(DateTimeOffset now)
    {
        if (Type != TransactionType.Charge) throw new DomainException("คืนเงินได้เฉพาะรายการที่เรียกเก็บสำเร็จ");
        return new Transaction
        {
            UserId = UserId, Type = TransactionType.Refund, Amount = Amount, Plan = Plan, Cycle = Cycle,
            RefundOfId = Id, CreatedAt = now,
        };
    }

    /// <summary>A failed charge paid later (recorded by the platform admin).</summary>
    public void MarkPaid(DateTimeOffset now)
    {
        if (Type != TransactionType.Failed) throw new DomainException("รายการนี้ไม่ได้ล้มเหลว");
        Type = TransactionType.Charge;
        CreatedAt = now;
    }

    public static Transaction FailedCharge(Guid userId, int amount, PlanKey plan, BillingCycle cycle, DateTimeOffset now) =>
        new() { UserId = userId, Type = TransactionType.Failed, Amount = amount, Plan = plan, Cycle = cycle, CreatedAt = now };
}

public class Promo : Entity
{
    public static readonly string[] Discounts = ["d10", "d20", "d30", "dFree"];

    public string Code { get; private set; } = "";
    public string Discount { get; private set; } = "";
    public int Uses { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public bool Active { get; private set; }

    private Promo() { } // EF Core

    public static Promo Create(string code, string discount, DateTimeOffset expiresAt)
    {
        var c = NormalizeCode(code);
        if (c.Length is < 3 or > 30 || !c.All(char.IsLetterOrDigit)) throw new DomainException("โค้ดต้องเป็นตัวอักษรหรือตัวเลข 3-30 ตัว");
        if (!Discounts.Contains(discount)) throw new DomainException("ส่วนลดไม่ถูกต้อง");
        return new Promo { Code = c, Discount = discount, ExpiresAt = expiresAt, Active = true };
    }

    public static string NormalizeCode(string? code) => (code ?? "").Trim().ToUpperInvariant();

    public bool IsUsable(DateTimeOffset now) => Active && now <= ExpiresAt;

    public void Use(DateTimeOffset now)
    {
        if (!IsUsable(now)) throw new DomainException("โค้ดส่วนลดนี้หมดอายุหรือถูกปิดแล้ว");
        Uses++;
    }

    public void SetActive(bool active) => Active = active;
}
