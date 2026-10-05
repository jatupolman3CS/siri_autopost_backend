using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

// Until accounts can be connected through the extension, every new workspace starts with the
// sample accounts, Facebook groups and snippets from the AutoPost Dashboard design. It adds no posts:
// the calendar, the queue and the error reports only ever show what the schedules and the extension made.
// Adds entities only; the caller's unit of work saves them.
public sealed class DemoWorkspaceSeeder(AppDbContext db, TimeProvider clock) : IWorkspaceSeeder
{
    public static readonly string[] Groups =
    [
        "ขายของบ้านและสวน", "ตลาดนัดออนไลน์ กทม.", "ของแต่งบ้านมือหนึ่ง", "แม่บ้านยุคใหม่", "ซื้อขายเฟอร์นิเจอร์",
        "คนรักต้นไม้", "ของใช้ในบ้านราคาส่ง", "ตกแต่งคอนโด", "บ้านและสวน DIY", "ตลาดออนไลน์ นนทบุรี",
        "ของมือหนึ่งราคาโรงงาน", "แม่ค้าออนไลน์รวมพลัง", "ชุมชนคนรักบ้าน", "ตลาดนัดปทุมธานี", "ซื้อขายคอนโด กทม.",
        "คอนโดติดรถไฟฟ้า", "เช่าคอนโดราคาถูก", "ตั๋วหนังราคาถูก", "ซื้อขายตั๋วหนัง-คอนเสิร์ต", "โปรโมชันโรงหนัง",
    ];

    public Task SeedAsync(Workspace ws, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var i = 0;
        SocialAccount Add(Platform p, string name, string handle, string target, AccountHealth health = AccountHealth.Ok, string[]? groups = null)
        {
            var a = SocialAccount.Create(ws.Id, p, name, handle, target, health, groups, i++);
            db.Accounts.Add(a);
            return a;
        }

        Add(Platform.Fb, ws.Name, $"เพจ Facebook · {Groups.Length} กลุ่ม", "เพจ", groups: Groups);
        Add(Platform.Fb, "Nattaya S.", "โปรไฟล์ Facebook", "ไทม์ไลน์", AccountHealth.Warn);
        Add(Platform.Ig, "@baandee.living", "Instagram", "ฟีด");
        Add(Platform.X, "@baandee_th", "X", "ไทม์ไลน์");
        Add(Platform.Tt, "@baandee.living", "TikTok", "โปรไฟล์", AccountHealth.Relogin);
        Add(Platform.Line, "@baandee", "LINE OA · 4,120 เพื่อน", "บรอดแคสต์");
        Add(Platform.Th, "@baandee.living", "Threads", "โปรไฟล์");

        (string Title, string Text, int Used)[] snippets =
        [
            ("ปิดท้ายโพสต์ขาย", "สนใจทักแชทได้เลย ส่งฟรีเมื่อสั่งครบ 990 บาท", 42),
            ("แฮชแท็กประจำ", "#บ้านดี #ของแต่งบ้าน #BaanDeeLiving #แต่งบ้าน", 58),
            ("เงื่อนไขโปรโมชัน", "โปรโมชันถึง 15 ต.ค. หรือจนกว่าสินค้าจะหมด", 12),
            ("คำถามที่พบบ่อย: การจัดส่ง", "จัดส่งภายใน 1–2 วันทำการ กรุงเทพฯ และปริมณฑลถึงภายในวันถัดไป", 7),
        ];
        foreach (var s in snippets) db.Snippets.Add(Snippet.Create(ws.Id, s.Title, s.Text, now, s.Used));
        return Task.CompletedTask;
    }
}
