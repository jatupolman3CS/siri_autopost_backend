using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

// Until accounts can be connected through the extension, every new workspace starts with the
// sample accounts, Facebook groups and snippets from the AutoPost Dashboard design, plus a
// week of sample post history, today's queue and the design's error reports.
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

    private static readonly string[] Contents =
    [
        "โปรโมชันต้นเดือน ชั้นวางของไม้สัก ลด 30% ส่งฟรีทั่วไทย สั่งได้เลยทางแชท",
        "ผ้าปูที่นอนคอตตอน 100% มาใหม่ 6 สี เริ่มต้น 590 บาท",
        "รีวิวจากลูกค้า โต๊ะทำงานพับได้ หลังใช้งานจริง 3 เดือน",
        "เคล็ดลับจัดบ้านให้โล่งใน 15 นาที",
        "ไลฟ์สดคืนนี้ 2 ทุ่ม เคลียร์สต็อกโคมไฟตั้งโต๊ะ",
        "เปิดพรีออเดอร์ตู้เก็บของขนาดเล็ก รอบส่ง 15 ต.ค.",
        "ขอบคุณที่ไว้วางใจ ครบ 1,000 ออเดอร์แล้ว",
        "คู่มือเลือกที่นอนให้เหมาะกับคนปวดหลัง",
    ];

    // Sample times are laid out on the Thai calendar day (08:00–21:00).
    private static readonly TimeSpan ThaiOffset = TimeSpan.FromHours(7);

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

        var page = Add(Platform.Fb, ws.Name, $"เพจ Facebook · {Groups.Length} กลุ่ม", "เพจ", groups: Groups);
        var profile = Add(Platform.Fb, "Nattaya S.", "โปรไฟล์ Facebook", "ไทม์ไลน์", AccountHealth.Warn);
        var ig = Add(Platform.Ig, "@baandee.living", "Instagram", "ฟีด");
        var x = Add(Platform.X, "@baandee_th", "X", "ไทม์ไลน์");
        var tt = Add(Platform.Tt, "@baandee.living", "TikTok", "โปรไฟล์", AccountHealth.Relogin);
        var line = Add(Platform.Line, "@baandee", "LINE OA · 4,120 เพื่อน", "บรอดแคสต์");
        var th = Add(Platform.Th, "@baandee.living", "Threads", "โปรไฟล์");

        // Group posts make up half of the sample traffic, as in the design.
        SocialAccount[] mix = [page, page, page, page, page, ig, ig, x, line, th, profile];
        AddPostHistory(ws, mix, now);
        AddErrorReports(ws, now, page, tt, ig, x);

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

    // A week back and a week ahead: finished posts before now, queued posts after it.
    private void AddPostHistory(Workspace ws, SocialAccount[] mix, DateTimeOffset now)
    {
        var rnd = new Random(20261003);
        var today = new DateTimeOffset(now.ToOffset(ThaiOffset).Date, ThaiOffset);
        for (var day = -7; day <= 7; day++)
        {
            var date = today.AddDays(day);
            var n = day == 0 ? 12 : day < 0 ? 5 + rnd.Next(3) : 3 + rnd.Next(3);
            for (var k = 0; k < n; k++)
            {
                var minute = 8 * 60 + (int)(13 * 60 * (k + 0.2 + rnd.NextDouble() * 0.6) / n);
                var at = date.AddMinutes(minute);
                var acc = mix[rnd.Next(mix.Length)];
                var target = acc.PostsToGroups ? acc.Groups[rnd.Next(acc.Groups.Count)] : acc.DefaultTarget;
                var status = at > now ? PostStatus.Queued : rnd.NextDouble() < 0.04 ? PostStatus.Skipped : PostStatus.Success;
                db.Posts.Add(Post.Record(ws.Id, acc, target, Contents[rnd.Next(Contents.Length)], at, status, null, now));
            }
        }
    }

    // The design's open error reports, at its Thai times of day (moved a day back while still ahead of now).
    private void AddErrorReports(Workspace ws, DateTimeOffset now, SocialAccount page, SocialAccount tt, SocialAccount ig, SocialAccount x)
    {
        var today = new DateTimeOffset(now.ToOffset(ThaiOffset).Date, ThaiOffset);
        void Fail(SocialAccount a, string target, int content, int daysAgo, int hour, int minute, FailureCode code)
        {
            var at = today.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);
            while (at > now) at = at.AddDays(-1);
            db.Posts.Add(Post.Record(ws.Id, a, target, Contents[content], at,
                code == FailureCode.PendingApproval ? PostStatus.Pending : PostStatus.Failed, code, now));
        }

        Fail(page, "ตลาดนัดออนไลน์ กทม.", 0, 0, 9, 12, FailureCode.RateLimit);
        Fail(tt, tt.DefaultTarget, 4, 0, 8, 40, FailureCode.Session);
        Fail(ig, ig.DefaultTarget, 1, 1, 18, 5, FailureCode.Network);
        Fail(page, "คนรักต้นไม้", 3, 1, 14, 20, FailureCode.PendingApproval);
        Fail(x, x.DefaultTarget, 2, 2, 11, 0, FailureCode.MediaTooLarge);
    }
}
