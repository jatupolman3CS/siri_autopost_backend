using System.Net;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>A one-page statement of a recorded charge or refund, for printing or saving as PDF from the browser.</summary>
public static class StatementHtml
{
    private static readonly TimeSpan Thai = TimeSpan.FromHours(7);

    public static string Render(StatementDto s)
    {
        var refund = s.Type == TransactionType.Refund;
        var title = refund ? "ใบแสดงรายการคืนเงิน" : "ใบแสดงรายการเรียกเก็บเงิน";
        var date = s.CreatedAt.ToOffset(Thai);
        var plan = char.ToUpperInvariant(s.Plan.ToString()[0]) + s.Plan.ToString()[1..].ToLowerInvariant();
        var cycle = s.Cycle == BillingCycle.Year ? "รายปี" : "รายเดือน";
        var status = s.Type switch
        {
            TransactionType.Charge => "บันทึกยอดแล้ว",
            TransactionType.Refund => "คืนเงินแล้ว",
            _ => "ตัดบัตรไม่สำเร็จ",
        };
        static string E(string? v) => WebUtility.HtmlEncode(v ?? "");
        var rows = new (string Label, string Value)[]
        {
            ("เลขที่รายการ", s.Id.ToString("N")[..12].ToUpperInvariant()),
            ("วันที่", $"{date:dd/MM/yyyy HH:mm} น. (เวลาไทย)"),
            ("ลูกค้า", $"{s.CustomerName} ({s.CustomerEmail})"),
            ("แผน", $"{plan} · {cycle}"),
            ("โค้ดส่วนลด", string.IsNullOrEmpty(s.PromoCode) ? "-" : s.PromoCode),
            ("สถานะ", status),
        };
        var table = string.Concat(rows.Select(r => $"<tr><th>{E(r.Label)}</th><td>{E(r.Value)}</td></tr>"));
        return $$"""
            <!doctype html>
            <html lang="th">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{E(title)}} {{s.Id.ToString("N")[..12].ToUpperInvariant()}}</title>
              <style>
                body { font: 15px/1.6 system-ui, "Noto Sans Thai", sans-serif; color: #111; max-width: 640px; margin: 40px auto; padding: 0 20px; }
                h1 { font-size: 22px; margin: 0 0 4px; }
                .sub { color: #666; margin: 0 0 24px; }
                table { width: 100%; border-collapse: collapse; }
                th, td { text-align: left; padding: 8px 0; border-bottom: 1px solid #e5e5e5; vertical-align: top; }
                th { width: 38%; color: #555; font-weight: 500; }
                .amount { font-size: 28px; font-weight: 700; margin: 24px 0 8px; }
                .note { color: #666; font-size: 13px; margin-top: 28px; }
                @media print { body { margin: 0; } }
              </style>
            </head>
            <body>
              <h1>AutoPost · {{E(title)}}</h1>
              <p class="sub">เอกสารสำหรับตรวจสอบรายการในบัญชีของคุณ</p>
              <table>{{table}}</table>
              <div class="amount">{{(refund ? "−" : "")}}฿{{s.Amount:N0}}</div>
              <p class="note">เอกสารนี้แสดงยอดที่บันทึกไว้ในระบบ AutoPost ซึ่งยังไม่ได้เชื่อมระบบตัดบัตร จึงไม่ใช่ใบเสร็จรับเงินหรือใบกำกับภาษี</p>
            </body>
            </html>
            """;
    }
}
