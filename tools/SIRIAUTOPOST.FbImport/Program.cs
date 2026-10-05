// Imports the posts of a Facebook "Download your information" archive (posts/profile_posts_*.json) into
// IMPORTED_POSTS and uploads their photos/videos to Cloudflare R2. Safe to run again: posts already imported
// (same SourceKey) are skipped and objects that already exist in the bucket are not uploaded twice.
//
//   dotnet run --project tools/SIRIAUTOPOST.FbImport -- --archive <folder holding this_profile's_activity_across_facebook>
//       --owner <email of the workspace owner> [--workspace <id>] [--dry-run]
//
// Settings (environment variables; a .env in the current folder or above is read too):
//   ConnectionStrings__Default, AppSettings__R2__{Endpoint,BucketName,PublicBaseUrl,AccessKeyId,SecretAccessKey}
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIRIAUTOPOST.Application.Common;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Infrastructure.Data;

const string Source = "archive";
const string OldSource = "facebook"; // first version of this tool
const string CampaignId = "import-archive";

LoadDotEnv();
var opt = ParseArgs(args);
var archive = opt.GetValueOrDefault("archive") ?? Directory.GetCurrentDirectory();
var dry = opt.ContainsKey("dry-run");
static string Env(string k) => Environment.GetEnvironmentVariable(k) ?? "";

var postFiles = Directory.EnumerateFiles(archive, "profile_posts_*.json", SearchOption.AllDirectories).OrderBy(x => x).ToList();
if (postFiles.Count == 0) { Console.Error.WriteLine("ไม่พบ profile_posts_*.json ใน " + archive); return 1; }
// Media uris start with the archive's top folder name, so they resolve from the folder above it.
var root = new DirectoryInfo(Path.GetDirectoryName(postFiles[0])!).Parent!.Parent!.FullName;

var bucket = Env("AppSettings__R2__BucketName") is { Length: > 0 } b1 ? b1 : Env("R2__BucketName");
var publicBase = (Env("AppSettings__R2__PublicBaseUrl") is { Length: > 0 } pb1 ? pb1 : Env("R2__PublicBaseUrl")).TrimEnd('/');
AmazonS3Client? s3 = null;
if (!dry)
{
    var ep = Env("AppSettings__R2__Endpoint") is { Length: > 0 } ep1 ? ep1 : Env("R2__Endpoint");
    var ak = Env("AppSettings__R2__AccessKeyId") is { Length: > 0 } a1 ? a1 : Env("R2__AccessKeyId");
    var sk = Env("AppSettings__R2__SecretAccessKey") is { Length: > 0 } s1 ? s1 : Env("R2__SecretAccessKey");
    if (ep == "" || bucket == "" || ak == "" || sk == "") { Console.Error.WriteLine("ขาดค่า R2: Endpoint/BucketName/AccessKeyId/SecretAccessKey"); return 1; }
    s3 = new AmazonS3Client(new BasicAWSCredentials(ak, sk),
        new AmazonS3Config { ServiceURL = ep, ForcePathStyle = true, AuthenticationRegion = "auto" });
}

var cs = Env("ConnectionStrings__Default");
if (cs == "") { Console.Error.WriteLine("ขาด ConnectionStrings__Default"); return 1; }
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).UseSnakeCaseNamingConvention().Options);

if (!dry) await db.Database.MigrateAsync();
Guid workspaceId;
if (opt.TryGetValue("workspace", out var w)) workspaceId = Guid.Parse(w);
else
{
    var email = (opt.GetValueOrDefault("owner") ?? "").Trim().ToLowerInvariant();
    if (email == "")
    {
        Console.Error.WriteLine("ระบุ --owner <อีเมล> หรือ --workspace <id>. ผู้ใช้ที่มี:");
        foreach (var u in await db.Users.OrderBy(x => x.CreatedAt).Take(30).ToListAsync()) Console.Error.WriteLine("  " + u.Email);
        return 1;
    }
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);
    if (user is null) { Console.Error.WriteLine("ไม่พบผู้ใช้ --owner " + email); return 1; }
    var ws = await db.Workspaces.Where(x => x.OwnerId == user.Id).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
    if (ws is null) { Console.Error.WriteLine("ผู้ใช้นี้ไม่มี workspace"); return 1; }
    workspaceId = ws.Id;
}
Console.WriteLine($"workspace {workspaceId}{(dry ? " (dry-run)" : "")}");
foreach (var w2 in await db.Workspaces.Select(x => new { x.Id, x.Name, x.OwnerId }).ToListAsync())
    Console.WriteLine($"  workspace ในระบบ: {w2.Id} {w2.Name}{(w2.Id == workspaceId ? "  <-- ใช้อันนี้" : "")}");

// Rows and objects written by the first version used "facebook" as source and key prefix; move them.
if (!dry)
{
    await db.ImportedPosts.Where(x => x.WorkspaceId == workspaceId && x.Source == OldSource)
        .ExecuteUpdateAsync(x => x.SetProperty(y => y.Source, Source));
    var olds = await db.ImportedPosts.Where(x => x.WorkspaceId == workspaceId).ToListAsync();
    var moved = 0;
    foreach (var ip in olds)
    {
        if (!ip.Media.Any(m => m.Key.StartsWith("facebook/"))) continue;
        var fresh = new List<ImportedMedia>();
        foreach (var m in ip.Media)
        {
            if (m.Key.StartsWith("facebook/"))
            {
                var nk = "media/" + m.Key["facebook/".Length..];
                // The same file can belong to several posts, so it may already have been moved.
                if (!await Exists(s3!, bucket, nk))
                    await s3!.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = m.Key, DestinationBucket = bucket, DestinationKey = nk });
                await s3!.DeleteObjectAsync(bucket, m.Key);
                m.Key = nk;
                moved++;
            }
            fresh.Add(m);
        }
        ip.ReplaceMedia(fresh);
    }
    if (moved > 0) { await db.SaveChangesAsync(); Console.WriteLine($"ย้ายชื่อไฟล์ใน R2 {moved} ไฟล์ (facebook/ → media/)"); }
}

var have = (await db.ImportedPosts.Where(x => x.WorkspaceId == workspaceId && x.Source == Source)
    .Select(x => x.SourceKey).ToListAsync()).ToHashSet();

int added = 0, skipped = 0, files = 0, uploaded = 0, missing = 0;
foreach (var file in postFiles)
{
    using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
    foreach (var p in doc.RootElement.EnumerateArray())
    {
        var ts = p.GetProperty("timestamp").GetInt64();
        var title = p.TryGetProperty("title", out var t) ? Fix(t.GetString()) : null;
        string? postText = null, link = null;
        var medias = new List<JsonElement>();
        if (p.TryGetProperty("data", out var data))
            foreach (var d in data.EnumerateArray())
                if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("post", out var pt)) postText = Fix(pt.GetString());
        if (p.TryGetProperty("attachments", out var atts))
            foreach (var a in atts.EnumerateArray())
                foreach (var d in a.GetProperty("data").EnumerateArray())
                {
                    if (d.TryGetProperty("media", out var m)) medias.Add(m);
                    else if (d.TryGetProperty("external_context", out var ec) && ec.TryGetProperty("url", out var u) && !string.IsNullOrEmpty(u.GetString())) link ??= u.GetString();
                }
        // A photo post carries its caption on the media instead of in "post".
        if (string.IsNullOrWhiteSpace(postText))
            postText = medias.Select(m => m.TryGetProperty("description", out var ds) ? Fix(ds.GetString()) : null)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var text = (postText ?? "").Trim();

        var key = ts + "-" + Hash(title + "|" + text + "|" + string.Join(",", medias.Select(m => m.GetProperty("uri").GetString())));
        if (have.Contains(key)) { skipped++; continue; }

        var list = new List<ImportedMedia>();
        foreach (var m in medias)
        {
            var uri = m.GetProperty("uri").GetString()!;
            var path = Path.Combine(root, uri.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { missing++; continue; }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var objKey = $"media/{Hash(uri)}{ext}";
            var ct = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".mp4" => "video/mp4", ".mov" => "video/quicktime", _ => "application/octet-stream" };
            if (s3 is not null && !await Exists(s3, bucket, objKey))
            {
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket, Key = objKey, FilePath = path, ContentType = ct,
                    DisablePayloadSigning = true, DisableDefaultChecksumValidation = true,
                });
                uploaded++;
            }
            files++;
            list.Add(new ImportedMedia { Key = objKey, Url = publicBase == "" ? objKey : publicBase + "/" + objKey, ContentType = ct, Size = new FileInfo(path).Length, SourcePath = uri });
        }

        have.Add(key);
        added++;
        if (dry) continue;
        db.ImportedPosts.Add(ImportedPost.Create(workspaceId, Source, key, DateTimeOffset.FromUnixTimeSeconds(ts), title, text, link, list, DateTimeOffset.UtcNow));
        if (added % 25 == 0) { await db.SaveChangesAsync(); Console.WriteLine($"... {added} โพสต์, อัปโหลด {uploaded} ไฟล์"); }
    }
}
if (!dry) await db.SaveChangesAsync();
Console.WriteLine($"นำเข้า: เพิ่ม {added} โพสต์, ข้าม {skipped}, ไฟล์ {files} (อัปโหลดใหม่ {uploaded}), ไฟล์หาย {missing}");

if (dry) return 0;

// Keep every address in step with the configured public base (it may be set up after the first import).
var all = await db.ImportedPosts.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.PostedAt).ToListAsync();
if (publicBase != "")
    foreach (var ip in all)
        ip.ReplaceMedia(ip.Media.Select(m => { m.Url = publicBase + "/" + m.Key; return m; }).ToList());
await db.SaveChangesAsync();

// 0) The web app's "ชุดโพสต์": a collection of posts whose media are library files that point at the bucket.
{
    const string CollName = "นำเข้าจากไฟล์เก่า";
    var now = DateTimeOffset.UtcNow;
    var coll = await db.Collections.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Name == CollName);
    if (coll is null)
    {
        coll = PostCollection.Create(workspaceId, CollName, "โพสต์และรูปที่นำเข้าจากไฟล์ส่งออก", now, await db.Collections.CountAsync(x => x.WorkspaceId == workspaceId));
        db.Collections.Add(coll);
    }
    // The imported files go into their own library folder.
    var folder = await db.MediaFolders.FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Name == CollName);
    if (folder is null) { folder = MediaFolder.Create(workspaceId, CollName, now); db.MediaFolders.Add(folder); }
    static string KeyOf(string url) { var i = url.IndexOf("/media/", StringComparison.Ordinal); return i < 0 ? url : url[(i + 1)..]; }
    var lib = new Dictionary<string, MediaFile>();
    foreach (var f in await db.Media.Where(x => x.WorkspaceId == workspaceId && x.ExternalUrl != null).ToListAsync()) lib[KeyOf(f.ExternalUrl!)] = f;
    int newMedia = 0, newPosts = 0, tooLong = 0;
    var inColl = db.CollectionMembers.Where(m => m.CollectionId == coll.Id).Select(m => m.PostId);
    var existing = (await db.CollectionPosts.Where(x => inColl.Contains(x.Id)).ToListAsync())
        .Select(x => x.Text + "|" + string.Join(",", x.MediaIds)).ToHashSet();
    foreach (var ip in all.AsEnumerable().Reverse())
    {
        var text = ip.Text.Trim();
        if (text.Length == 0) continue;
        if (text.Length > CollectionPost.MaxTextLength) { text = text[..CollectionPost.MaxTextLength]; tooLong++; }
        var ids = new List<Guid>();
        foreach (var m in ip.Media.Take(CollectionPost.MaxMedia))
        {
            if (!lib.TryGetValue(m.Key, out var f))
            {
                f = MediaFile.CreateExternal(workspaceId, Path.GetFileNameWithoutExtension(m.Key), m.ContentType, m.Size, "r2://" + m.Key, now);
                f.MoveToFolder(folder.Id);
                db.Media.Add(f);
                lib[m.Key] = f;
                newMedia++;
            }
            else if (f.ExternalUrl != "r2://" + m.Key) f.MoveTo("r2://" + m.Key); // the API reads the private bucket itself
            ids.Add(f.Id);
        }
        ids = ids.Distinct().ToList();
        if (!existing.Add(text + "|" + string.Join(",", ids))) continue;
        var created = CollectionPost.Create(coll, text, ids, ip.PostedAt);
        db.CollectionPosts.Add(created);
        db.CollectionMembers.Add(CollectionMember.Create(workspaceId, coll.Id, created.Id, ip.PostedAt));
        newPosts++;
    }
    foreach (var f in lib.Values) if (f.FolderId is null) f.MoveToFolder(folder.Id);
    await db.SaveChangesAsync();
    // Remove copies made by an earlier run (same text and media): keep the oldest.
    var dups = (await db.CollectionPosts.Where(x => inColl.Contains(x.Id)).OrderBy(x => x.CreatedAt).ThenBy(x => x.UpdatedAt).ToListAsync())
        .GroupBy(x => x.Text + "|" + string.Join(",", x.MediaIds)).SelectMany(g => g.Skip(1)).ToList();
    if (dups.Count > 0) { db.CollectionPosts.RemoveRange(dups); await db.SaveChangesAsync(); }
    Console.WriteLine($"ชุดโพสต์ในเว็บ \"{CollName}\": รวม {await db.CollectionMembers.CountAsync(m => m.CollectionId == coll.Id)} โพสต์, ลบซ้ำ {dups.Count}, โพสต์ใหม่ {newPosts} (ตัดข้อความที่ยาวเกิน {tooLong}), ไฟล์ในคลังใหม่ {newMedia}");
}

// 1) A campaign ("ชุดโพสต์") in the device's extension settings: one post per distinct text + media set.
var device = opt.TryGetValue("device", out var dv)
    ? await db.Devices.FirstOrDefaultAsync(x => x.Id == Guid.Parse(dv))
    : await db.Devices.Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
if (device is null) Console.WriteLine("ข้ามชุดโพสต์: workspace นี้ยังไม่มีอุปกรณ์ที่จับคู่ (ใช้ --device <id>)");
else
{
    var seen = new HashSet<string>();
    var posts = new JsonArray();
    foreach (var ip in all.AsEnumerable().Reverse())
    {
        if (ip.Text.Length == 0 && ip.Media.Count == 0) continue;
        var urls = ip.Media.Select(m => m.Url).Where(u => u.StartsWith("http")).ToList();
        if (!seen.Add(Hash(ip.Text + "|" + string.Join(",", ip.Media.Select(m => m.Key))))) continue;
        posts.Add(new JsonObject
        {
            ["id"] = "imp-" + ip.SourceKey.Split('-')[1][..12],
            ["text"] = ip.Text,
            ["imageIds"] = new JsonArray(),
            ["imageUrls"] = new JsonArray(urls.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
            ["groupUrls"] = new JsonArray(),
        });
    }
    var cfg = await db.ExtensionConfigs.FirstOrDefaultAsync(x => x.DeviceId == device.Id);
    if (cfg is null) { cfg = ExtensionConfig.Create(workspaceId, device.Id); db.ExtensionConfigs.Add(cfg); }
    var sroot = string.IsNullOrEmpty(cfg.Settings) ? new JsonObject { ["version"] = 2, ["campaigns"] = new JsonArray() } : JsonNode.Parse(cfg.Settings)!.AsObject();
    var camps = sroot["campaigns"] as JsonArray ?? (JsonArray)(sroot["campaigns"] = new JsonArray());
    var existing = camps.OfType<JsonObject>().FirstOrDefault(c => (string?)c["id"] == CampaignId);
    if (existing is not null) camps.Remove(existing);
    // Disabled and without groups: nothing is posted until the owner picks groups and turns it on.
    camps.Add(new JsonObject
    {
        ["id"] = CampaignId, ["name"] = "นำเข้าจากไฟล์เก่า", ["enabled"] = false,
        ["groups"] = new JsonArray(), ["posts"] = posts, ["leadImageIds"] = new JsonArray(),
    });
    var json = sroot.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    var changed = cfg.Save(json, ExtensionSettings.HasContent(json), null, false, DateTimeOffset.UtcNow);
    await db.SaveChangesAsync();
    Console.WriteLine($"ชุดโพสต์ \"นำเข้าจากไฟล์เก่า\": {posts.Count} แบบโพสต์ ({(changed ? "บันทึกแล้ว" : "ไม่เปลี่ยน")}) อุปกรณ์ {device.Name}");
}

// 2) History rows in POSTS (status success) on one account, so they show in the dashboard.
var accountArg = opt.GetValueOrDefault("account");
var accounts = await db.Accounts.Where(x => x.WorkspaceId == workspaceId && x.Platform == SIRIAUTOPOST.Domain.Enums.Platform.Fb).OrderBy(x => x.SortOrder).ToListAsync();
var account = accountArg is null ? accounts.FirstOrDefault(x => x.DeviceId != null) : accounts.FirstOrDefault(x => x.Name == accountArg || x.Id.ToString() == accountArg);
if (account is null)
{
    Console.WriteLine("ข้ามประวัติโพสต์: ระบุ --account <ชื่อหรือ id>. บัญชี Facebook ที่มี:");
    foreach (var a in accounts) Console.WriteLine($"  {a.Id}  {a.Name}{(a.DeviceId != null ? " (เชื่อมอุปกรณ์)" : "")}");
}
else
{
    var haveAt = (await db.Posts.Where(x => x.AccountId == account.Id).Select(x => x.ScheduledAt).ToListAsync()).ToHashSet();
    var n = 0;
    foreach (var ip in all)
    {
        if (ip.Text.Length == 0 || haveAt.Contains(ip.PostedAt)) continue;
        db.Posts.Add(Post.Record(workspaceId, account, "ไทม์ไลน์", ip.Text.Length > Post.MaxContentLength ? ip.Text[..Post.MaxContentLength] : ip.Text, ip.PostedAt, SIRIAUTOPOST.Domain.Enums.PostStatus.Success, null, DateTimeOffset.UtcNow));
        haveAt.Add(ip.PostedAt);
        n++;
    }
    await db.SaveChangesAsync();
    Console.WriteLine($"ประวัติโพสต์: เพิ่ม {n} แถวในบัญชี {account.Name}");
}
return 0;

// Facebook exports UTF-8 text as if each byte were a Latin-1 character; undo that.
static string? Fix(string? s)
{
    if (s is null) return null;
    foreach (var c in s) if (c > 0xFF) return s;
    try { return new UTF8Encoding(false, true).GetString(Encoding.Latin1.GetBytes(s)); } catch { return s; }
}

static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();

static async Task<bool> Exists(AmazonS3Client s3, string bucket, string key)
{
    try { await s3.GetObjectMetadataAsync(bucket, key); return true; }
    catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
}

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
