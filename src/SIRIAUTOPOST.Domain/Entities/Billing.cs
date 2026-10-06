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
    /// <summary>Facebook groups and pages (links of the link sets), all workspaces of the owner together.</summary>
    public int? Groups { get; private set; }
    /// <summary>Files in the image library, all workspaces of the owner together.</summary>
    public int? Images { get; private set; }
    /// <summary>Posts in the post library, all workspaces of the owner together.</summary>
    public int? LibraryPosts { get; private set; }

    /// <summary>Stripe cannot charge less than 10 baht, so a paid plan costs at least that a month.</summary>
    public const int MinPaidPrice = 10;

    private PlanSetting() { } // EF Core

    public static PlanSetting Create(
        PlanKey key, int price, int? accounts, int? posts, int? devices, int? seats, int? groups = null, int? images = null, int? libraryPosts = null) =>
        new()
        {
            Key = key, Price = price, Accounts = accounts, Posts = posts, Devices = devices, Seats = seats,
            Groups = groups, Images = images, LibraryPosts = libraryPosts,
        };

    /// <summary>
    /// The packages (the platform admin edits the numbers; which features each one includes is fixed by
    /// <see cref="PlanFeatures"/>). Free is for trying it out, Basic for one shop, Pro for a seller with several
    /// browsers, Agency (shown as Premium) for agencies and heavy users: nothing is capped but the seats.
    /// </summary>
    public static IReadOnlyList<PlanSetting> Defaults =>
    [
        Create(PlanKey.Free, 0, 1, 10, 1, 1, groups: 10, images: 20, libraryPosts: 20),
        Create(PlanKey.Basic, 290, 2, 50, 1, 1, groups: 50, images: 200, libraryPosts: 200),
        Create(PlanKey.Pro, 790, 10, 300, 3, 3, groups: 300, images: 1000, libraryPosts: 1000),
        Create(PlanKey.Agency, 1990, null, null, null, 10),
    ];

    public void Update(int price, int? accounts, int? posts, int? devices, int? seats, int? groups = null, int? images = null, int? libraryPosts = null)
    {
        if (price < 0 || price > 1_000_000) throw new DomainException("ราคาไม่ถูกต้อง");
        if (Key == PlanKey.Free && price != 0) throw new DomainException("แผน Free ต้องไม่มีค่าใช้จ่าย");
        if (Key != PlanKey.Free && price < MinPaidPrice) throw new DomainException($"แผนที่เสียเงินต้องมีราคาอย่างน้อย {MinPaidPrice} บาท (ขั้นต่ำที่ Stripe เรียกเก็บได้)");
        if (new[] { accounts, posts, devices, seats, groups, images, libraryPosts }.Any(v => v is < 1)) throw new DomainException("ขีดจำกัดต้องมากกว่า 0 หรือเว้นว่างเพื่อไม่จำกัด");
        Price = price;
        Accounts = accounts;
        Posts = posts;
        Devices = devices;
        Seats = seats;
        Groups = groups;
        Images = images;
        LibraryPosts = libraryPosts;
    }
}

/// <summary>
/// What each package includes besides its numbers. Fixed in code (the numbers are the admin's to edit): a feature
/// that costs the platform money (AI) or is the reason for a higher price (bump, client reports) is not a setting.
/// </summary>
public static class PlanFeatures
{
    public const string AdvancedAntiBan = "advanced_anti_ban";
    public const string Notifications = "notifications";
    public const string AutoReply = "auto_reply";
    public const string Ai = "ai";
    public const string Bump = "bump";
    public const string ClientReports = "client_reports";

    /// <summary>Every feature key, in the order the plan cards list them.</summary>
    public static readonly IReadOnlyList<string> All = [AdvancedAntiBan, Notifications, AutoReply, Ai, Bump, ClientReports];

    public static IReadOnlyList<string> For(PlanKey plan) => plan switch
    {
        PlanKey.Free or PlanKey.Basic => [],
        PlanKey.Pro => [AdvancedAntiBan, Notifications, AutoReply, Ai],
        _ => All,
    };

    public static bool Has(PlanKey plan, string feature) => For(plan).Contains(feature);
}

/// <summary>
/// The platform admin's switch for testing a real payment (one row). While it is on, Stripe Checkout and a plan change
/// of the listed customers charge <see cref="Amount"/> baht for the billing period instead of the plan's price, so a
/// real card or PromptPay transfer can be tried for a few baht. The plan the customer gets still follows what they
/// chose: only the money differs. Nobody outside <see cref="Emails"/> is touched, so real customers keep paying real prices.
/// </summary>
public class PaymentOverride
{
    public const int MaxEmails = 20;
    public const int MaxAmount = 1_000_000;

    /// <summary>Always 1: there is one setting for the platform.</summary>
    public int Id { get; private set; } = 1;
    public bool Enabled { get; private set; }
    /// <summary>Baht charged per billing period (a yearly plan pays this once a year); at least what Stripe can charge.</summary>
    public int Amount { get; private set; } = PlanSetting.MinPaidPrice;
    /// <summary>Normalised e-mail addresses of the customers it applies to.</summary>
    public List<string> Emails { get; private set; } = [];
    public DateTimeOffset UpdatedAt { get; private set; }

    private PaymentOverride() { } // EF Core

    public static PaymentOverride CreateDefault() => new();

    public void Update(bool enabled, int amount, IEnumerable<string> emails, DateTimeOffset now)
    {
        if (amount < PlanSetting.MinPaidPrice || amount > MaxAmount)
            throw new DomainException($"ยอดทดสอบต้องอยู่ระหว่าง {PlanSetting.MinPaidPrice} ถึง {MaxAmount:N0} บาท (ขั้นต่ำที่ Stripe เรียกเก็บได้ {PlanSetting.MinPaidPrice} บาท)");
        var list = emails.Select(User.NormalizeEmail).Where(e => e.Length > 0).Distinct().ToList();
        if (list.Count > MaxEmails) throw new DomainException($"ระบุอีเมลได้ไม่เกิน {MaxEmails} บัญชี");
        if (enabled && list.Count == 0) throw new DomainException("ต้องระบุอีเมลลูกค้าอย่างน้อย 1 บัญชีที่จะใช้ยอดทดสอบ (กันไม่ให้ลูกค้าจริงโดนเรียกเก็บยอดนี้)");
        Enabled = enabled;
        Amount = amount;
        Emails = list;
        UpdatedAt = now;
    }

    /// <summary>The baht to charge this customer for a billing period instead of the plan's price; null = the normal price.</summary>
    public int? ChargeFor(string email) => Enabled && Emails.Contains(User.NormalizeEmail(email)) ? Amount : null;
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
