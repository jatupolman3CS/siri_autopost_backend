// Puts the group links of an extension config (client/config/autopost-config.json) into one link set of a user's
// workspace. Safe to run again: links already in the set (same address) are skipped.
//
//   dotnet run --project tools/SIRIAUTOPOST.LinkImport -- --owner <email> [--config <file>] [--set <name>] [--apply]
//
// Without --apply nothing is written. Settings: ConnectionStrings__Default (a .env in the current folder or above is read).
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Services;
using SIRIAUTOPOST.Infrastructure.Data;

LoadDotEnv();
var opt = ParseArgs(args);
var apply = opt.ContainsKey("apply");
var email = (opt.GetValueOrDefault("owner") ?? "").Trim().ToLowerInvariant();
var configPath = opt.GetValueOrDefault("config") ?? Path.Combine("client", "config", "autopost-config.json");
var setName = opt.GetValueOrDefault("set") ?? "กลุ่มเดิม (นำเข้าจาก config)";
var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "";
if (cs == "" || email == "") { Console.Error.WriteLine("ต้องมี --owner <อีเมล> และ ConnectionStrings__Default"); return 1; }

var rows = new List<(string Name, string Url, string Code)>();
var cfg = JsonNode.Parse(File.ReadAllText(configPath))!;
foreach (var camp in cfg["settings"]!["campaigns"]!.AsArray())
    foreach (var g in camp!["groups"]!.AsArray())
    {
        var url = FacebookGroupUrl.Normalize((string?)g!["url"] ?? "");
        var code = ((string?)g["text"] ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        var name = ((string?)g["name"] ?? "").Trim();
        if (url is null || !SetLink.Fits(url, code) || name.Length > SetLink.MaxNameLength) { Console.WriteLine("ข้าม (ใช้ไม่ได้): " + g["url"]); continue; }
        rows.Add((name, url, code));
    }

await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);
var user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);
if (user is null) { Console.Error.WriteLine("ไม่พบผู้ใช้ " + email); return 1; }
var ws = await db.Workspaces.Where(x => x.OwnerId == user.Id).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
if (ws is null) { Console.Error.WriteLine("ผู้ใช้นี้ไม่มี workspace"); return 1; }
Console.WriteLine($"ผู้ใช้ {user.Email}  workspace {ws.Id} ({ws.Name})  {(apply ? "APPLY" : "dry-run")}");

var sets = await db.LinkSets.Where(x => x.WorkspaceId == ws.Id).OrderBy(x => x.SortOrder).ToListAsync();
Console.WriteLine($"ชุดลิงก์ที่มีอยู่: {sets.Count}");
var set = sets.FirstOrDefault(x => x.Name == setName);
var have = new HashSet<string>(FacebookGroupUrl.Comparer);
var order = 0;
if (set is not null)
{
    var existing = await db.SetLinks.Where(x => x.LinkSetId == set.Id).ToListAsync();
    foreach (var l in existing) have.Add(l.Url);
    order = existing.Count == 0 ? 0 : existing.Max(l => l.SortOrder) + 1;
}
var fresh = rows.Where(r => have.Add(r.Url)).ToList();
Console.WriteLine($"ใน config {rows.Count} กลุ่ม, เพิ่มใหม่ {fresh.Count}, ซ้ำ/มีแล้ว {rows.Count - fresh.Count}");
if (set is null && sets.Count >= LinkSet.MaxPerWorkspace) { Console.Error.WriteLine("ชุดลิงก์เต็ม"); return 1; }
if (fresh.Count + order > LinkSet.MaxLinks) { Console.Error.WriteLine("เกิน " + LinkSet.MaxLinks + " ลิงก์ต่อชุด"); return 1; }
if (!apply) return 0;

var now = DateTimeOffset.UtcNow;
if (set is null)
{
    set = LinkSet.Create(ws.Id, setName, null, now, sets.Count == 0 ? 0 : sets.Max(x => x.SortOrder) + 1);
    db.LinkSets.Add(set);
}
foreach (var r in fresh) db.SetLinks.Add(SetLink.Create(ws.Id, set.Id, r.Name, r.Url, r.Code, 0, now, order++));
await db.SaveChangesAsync();
Console.WriteLine($"เพิ่มแล้ว {fresh.Count} ลิงก์ในชุด '{set.Name}' ({set.Id})");
return 0;

static Dictionary<string, string> ParseArgs(string[] a)
{
    var r = new Dictionary<string, string>();
    for (var i = 0; i < a.Length; i++)
        if (a[i].StartsWith("--")) r[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    return r;
}

static void LoadDotEnv()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
    {
        var f = Path.Combine(d.FullName, ".env");
        if (!File.Exists(f)) continue;
        foreach (var line in File.ReadAllLines(f))
        {
            var l = line.Trim();
            var i = l.IndexOf('=');
            if (l.StartsWith('#') || i <= 0) continue;
            var k = l[..i].Trim();
            if (Environment.GetEnvironmentVariable(k) is null) Environment.SetEnvironmentVariable(k, l[(i + 1)..].Trim());
        }
        return;
    }
}
