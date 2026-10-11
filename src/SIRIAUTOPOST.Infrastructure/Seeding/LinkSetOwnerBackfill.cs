using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

// One link set, one extension (2026-10-10). Two start-up passes bring older data in line, and both are idempotent:
//   1. A set could name no extension ("automatic") and went to the first connected one, so with two extensions the sets
//      drifted between them whenever one went offline or was unbound. The sets that still name nobody in a workspace
//      with TWO OR MORE connected extensions are given to the extension they have been going to (the first connected
//      one, the old rule), which keeps what is queued where it is and makes the owner visible. A workspace with one
//      extension is left alone: "no choice" there is that extension.
//   2. A set could also name "other accounts" that posted the same text through THEIR extensions: that is gone. The list
//      is cleared and the posts those accounts still had queued from the set's schedules are removed (nothing that was
//      taken or went out is touched).
// Never blocks startup.
public static class LinkSetOwnerBackfill
{
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        if (bool.TryParse(sp.GetRequiredService<IConfiguration>()["LinkSets:BackfillOnStartup"], out var on) && !on) return;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seeding");
        try
        {
            var db = sp.GetRequiredService<AppDbContext>();

            var workspaces = await db.LinkSets.Where(s => s.PostAsAccountId == null).Select(s => s.WorkspaceId).Distinct().ToListAsync(ct);
            var pinned = 0;
            foreach (var workspaceId in workspaces)
            {
                var accounts = await db.Accounts.Where(a => a.WorkspaceId == workspaceId).OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct);
                var connected = accounts.Where(a => a.Platform == Platform.Fb && a.IsConnected).ToList();
                if (connected.Count < 2) continue;
                var sets = await db.LinkSets.Where(s => s.WorkspaceId == workspaceId && s.PostAsAccountId == null).ToListAsync(ct);
                pinned += LinkOwnership.PinUnassigned(sets, connected[0].Id).Count;
            }

            var withOthers = await db.LinkSets.Where(s => s.AccountIds.Count > 0).ToListAsync(ct);
            var removed = 0;
            if (withOthers.Count > 0)
            {
                var setIds = withOthers.Select(s => s.Id).ToList();
                var scheduleIds = await db.Schedules.Where(s => setIds.Contains(s.LinkSetId)).Select(s => s.Id).ToListAsync(ct);
                var queued = await db.Posts
                    .Where(p => p.ScheduleId != null && scheduleIds.Contains(p.ScheduleId.Value) && p.TargetKey != null &&
                                p.TargetKey.StartsWith("account:") && (p.Status == PostStatus.Queued || p.Status == PostStatus.Waiting))
                    .ToListAsync(ct);
                db.Posts.RemoveRange(queued);
                removed = queued.Count;
                foreach (var s in withOthers) s.Update(s.Name, s.PostAsAccountId, []);
            }

            if (pinned == 0 && withOthers.Count == 0) return;
            await db.SaveChangesAsync(ct);
            log.LogInformation("ชุดลิงก์ที่ยังไม่ระบุเครื่อง {Pinned} ชุด ถูกผูกกับเครื่องที่โพสต์อยู่เดิม; ล้างบัญชีอื่นของ {Cleared} ชุด และลบโพสต์ที่รอของบัญชีเหล่านั้น {Removed} โพสต์",
                pinned, withOthers.Count, removed);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The database may not be migrated yet (or not reachable, as when only the OpenAPI document is made).
            log.LogWarning(e, "ปรับชุดลิงก์ให้ใช้เครื่องเดียวตอนเริ่มระบบไม่สำเร็จ จะลองใหม่ตอนเริ่มครั้งหน้า");
        }
    }
}
