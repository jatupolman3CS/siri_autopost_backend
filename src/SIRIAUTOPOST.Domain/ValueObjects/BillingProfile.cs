using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

/// <summary>
/// A customer's billing preferences and the card on file. Only what a payment provider hands back
/// after tokenizing a card is kept here (brand, last four digits, expiry): never the number or the CVC.
/// </summary>
public class BillingProfile
{
    public bool NotifyFailed { get; set; } = true;
    public bool NotifyExpiring { get; set; } = true;
    public bool NotifyRenewal { get; set; }

    public string? CardBrand { get; set; }
    public string? CardLast4 { get; set; }
    public int? CardExpMonth { get; set; }
    public int? CardExpYear { get; set; }

    public bool HasCard => CardLast4 is not null;

    /// <summary>The last moment the card is valid: the end of its expiry month (UTC).</summary>
    public DateTimeOffset? CardValidThrough =>
        CardExpMonth is { } m && CardExpYear is { } y
            ? new DateTimeOffset(y, m, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1)
            : null;

    public static readonly string[] Brands = ["visa", "mastercard", "amex", "jcb", "unionpay", "card"];

    public static BillingProfile WithCard(BillingProfile current, string? brand, string last4, int expMonth, int expYear, DateTimeOffset now)
    {
        if (last4.Length != 4 || !last4.All(char.IsAsciiDigit)) throw new DomainException("เลข 4 ตัวท้ายของบัตรไม่ถูกต้อง");
        if (expMonth is < 1 or > 12) throw new DomainException("เดือนที่บัตรหมดอายุไม่ถูกต้อง");
        if (expYear < 2000 || expYear > 2100) throw new DomainException("ปีที่บัตรหมดอายุไม่ถูกต้อง");
        var through = new DateTimeOffset(expYear, expMonth, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        if (through <= now) throw new DomainException("บัตรนี้หมดอายุแล้ว");
        var kind = (brand ?? "").Trim().ToLowerInvariant();
        return Copy(current, c =>
        {
            c.CardBrand = Brands.Contains(kind) ? kind : "card";
            c.CardLast4 = last4;
            c.CardExpMonth = expMonth;
            c.CardExpYear = expYear;
        });
    }

    public static BillingProfile WithoutCard(BillingProfile current) =>
        Copy(current, c => (c.CardBrand, c.CardLast4, c.CardExpMonth, c.CardExpYear) = (null, null, null, null));

    public static BillingProfile WithNotifications(BillingProfile current, bool failed, bool expiring, bool renewal) =>
        Copy(current, c => (c.NotifyFailed, c.NotifyExpiring, c.NotifyRenewal) = (failed, expiring, renewal));

    // EF Core tracks owned JSON by reference: a changed profile is a new object.
    private static BillingProfile Copy(BillingProfile from, Action<BillingProfile> change)
    {
        var next = new BillingProfile
        {
            NotifyFailed = from.NotifyFailed, NotifyExpiring = from.NotifyExpiring, NotifyRenewal = from.NotifyRenewal,
            CardBrand = from.CardBrand, CardLast4 = from.CardLast4, CardExpMonth = from.CardExpMonth, CardExpYear = from.CardExpYear,
        };
        change(next);
        return next;
    }
}
