using System.Security.Cryptography;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// A client report link (Agency): a frozen copy of a report that anyone with the token can read until it expires.
public class ReportShare : Entity
{
    public const int MaxBrandLength = 120;
    public const int TokenLength = 43;
    public static readonly string[] Periods = ["week", "month"];
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public Guid WorkspaceId { get; private set; }
    /// <summary>43 URL-safe characters (256 random bits).</summary>
    public string Token { get; private set; } = "";
    public string Brand { get; private set; } = "";
    /// <summary>"week" or "month".</summary>
    public string Period { get; private set; } = "week";
    public bool ShowLogo { get; private set; }
    /// <summary>The shared report as JSON (the SharedReportDto the link shows).</summary>
    public string SnapshotJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    private ReportShare() { } // EF Core

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static ReportShare Create(Guid workspaceId, string token, string brand, string period, bool logo, string snapshotJson, DateTimeOffset now)
    {
        var b = (brand ?? "").Trim();
        if (b.Length == 0) throw new DomainException("กรุณาใส่ชื่อแบรนด์");
        if (b.Length > MaxBrandLength) throw new DomainException($"ชื่อแบรนด์ยาวเกิน {MaxBrandLength} ตัวอักษร");
        if (!Periods.Contains(period)) throw new DomainException("ช่วงเวลาของรายงานไม่ถูกต้อง");
        if (token is not { Length: TokenLength }) throw new DomainException("โทเคนไม่ถูกต้อง");
        return new ReportShare
        {
            WorkspaceId = workspaceId,
            Token = token,
            Brand = b,
            Period = period,
            ShowLogo = logo,
            SnapshotJson = snapshotJson,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
