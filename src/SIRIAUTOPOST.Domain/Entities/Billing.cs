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

    /// <summary>Stripe cannot charge less than 10 baht, so a paid plan costs at least that a month.</summary>
    public const int MinPaidPrice = 10;

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
        if (Key != PlanKey.Free && price < MinPaidPrice) throw new DomainException($"แผนที่เสียเงินต้องมีราคาอย่างน้อย {MinPaidPrice} บาท (ขั้นต่ำที่ Stripe เรียกเก็บได้)");
        if (new[] { accounts, posts, devices, seats }.Any(v => v is < 1)) throw new DomainException("ขีดจำกัดต้องมากกว่า 0 หรือเว้นว่างเพื่อไม่จำกัด");
        Price = price;
        Accounts = accounts;
        Posts = posts;
        Devices = devices;
        Seats = seats;
    }
}

/// <summary>The Stripe objects behind a ledger row (all optional: rows older than Stripe have none).</summary>
public sealed record PaymentRefs(string? InvoiceId = null, string? PaymentIntentId = null, string? ReceiptUrl = null);

// One money movement of a customer, written from what Stripe reports (invoice.paid, a refund...).
public class Transaction : Entity
{
    public Guid UserId { get; private set; }
    public TransactionType Type { get; private set; }
    /// <summary>Baht, always positive (a refund is money going back). Stripe works in satang, so it can have cents.</summary>
    public decimal Amount { get; private set; }
    public PlanKey Plan { get; private set; }
    public BillingCycle Cycle { get; private set; }
    public string? PromoCode { get; private set; }
    /// <summary>The charge a refund gives back.</summary>
    public Guid? RefundOfId { get; private set; }
    /// <summary>The Stripe invoice this row settles (unique): a redelivered event finds its row again.</summary>
    public string? StripeInvoiceId { get; private set; }
    /// <summary>What a refund of this charge is made against.</summary>
    public string? StripePaymentIntentId { get; private set; }
    /// <summary>The Stripe refund this row records (unique).</summary>
    public string? StripeRefundId { get; private set; }
    /// <summary>Stripe's hosted invoice page (receipt and PDF).</summary>
    public string? ReceiptUrl { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Transaction() { } // EF Core

    public static Transaction Charge(Guid userId, decimal amount, PlanKey plan, BillingCycle cycle, string? promo, DateTimeOffset now, PaymentRefs? stripe = null) =>
        new()
        {
            UserId = userId, Type = TransactionType.Charge, Amount = amount, Plan = plan, Cycle = cycle, PromoCode = promo, CreatedAt = now,
            StripeInvoiceId = stripe?.InvoiceId, StripePaymentIntentId = stripe?.PaymentIntentId, ReceiptUrl = stripe?.ReceiptUrl,
        };

    /// <summary>The admin can refund it through Stripe: it was paid there, and money actually moved.</summary>
    public bool CanRefund => Type == TransactionType.Charge && Amount > 0 && StripePaymentIntentId is not null;

    /// <summary>Money going back. <paramref name="amount"/> defaults to the whole charge.</summary>
    public Transaction RefundOf(DateTimeOffset now, decimal? amount = null, string? stripeRefundId = null)
    {
        if (Type != TransactionType.Charge) throw new DomainException("คืนเงินได้เฉพาะรายการที่เรียกเก็บสำเร็จ");
        var back = amount ?? Amount;
        if (back <= 0 || back > Amount) throw new DomainException("ยอดคืนเงินไม่ถูกต้อง");
        return new Transaction
        {
            UserId = UserId, Type = TransactionType.Refund, Amount = back, Plan = Plan, Cycle = Cycle,
            RefundOfId = Id, StripePaymentIntentId = StripePaymentIntentId, StripeRefundId = stripeRefundId, CreatedAt = now,
        };
    }

    /// <summary>A failed charge that Stripe collected after all (a retry or the customer's new card).</summary>
    public void MarkPaid(DateTimeOffset now, decimal amount, PaymentRefs? stripe = null)
    {
        if (Type != TransactionType.Failed) throw new DomainException("รายการนี้ไม่ได้ล้มเหลว");
        Type = TransactionType.Charge;
        Amount = amount;
        CreatedAt = now;
        StripePaymentIntentId = stripe?.PaymentIntentId ?? StripePaymentIntentId;
        ReceiptUrl = stripe?.ReceiptUrl ?? ReceiptUrl;
    }

    public static Transaction FailedCharge(Guid userId, decimal amount, PlanKey plan, BillingCycle cycle, DateTimeOffset now, PaymentRefs? stripe = null) =>
        new()
        {
            UserId = userId, Type = TransactionType.Failed, Amount = amount, Plan = plan, Cycle = cycle, CreatedAt = now,
            StripeInvoiceId = stripe?.InvoiceId, ReceiptUrl = stripe?.ReceiptUrl,
        };
}

/// <summary>
/// A payment event that was already handled: a Stripe event id, or "checkout:{session}" for the work shared by
/// the webhook and the return from Checkout. Stripe delivers events at least once, so the key (the primary key)
/// is written in the same save as the effects and a second delivery is skipped.
/// </summary>
public class ProcessedPaymentEvent
{
    public string Key { get; private set; } = "";
    public string Type { get; private set; } = "";
    public DateTimeOffset At { get; private set; }

    private ProcessedPaymentEvent() { } // EF Core

    public static ProcessedPaymentEvent Create(string key, string type, DateTimeOffset now) => new() { Key = key, Type = type, At = now };
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

    public void EnsureUsable(DateTimeOffset now)
    {
        if (!IsUsable(now)) throw new DomainException("โค้ดส่วนลดนี้หมดอายุหรือถูกปิดแล้ว");
    }

    /// <summary>Counts a redemption once its checkout is paid (the code may have expired while the customer paid).</summary>
    public void RecordUse() => Uses++;

    public void SetActive(bool active) => Active = active;
}
