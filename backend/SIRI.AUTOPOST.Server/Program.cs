using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SIRI.AUTOPOST.Server.Data;
using SIRI.AUTOPOST.Server.Endpoints;
using SIRI.AUTOPOST.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

const long MaxBody = 300L * 1024 * 1024; // videos are sent as base64 JSON
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxBody);

// ConnectionStrings:Default, else the shared .env's AppSettings__ConnectionStrings.
var connectionString = new[] { cfg.GetConnectionString("Default"), cfg["AppSettings:ConnectionStrings"] }
    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
    ?? throw new InvalidOperationException("ยังไม่ได้ตั้ง ConnectionStrings:Default (หรือ AppSettings__ConnectionStrings ใน .env)");
// Database:Name = own database on the same server (created on first start).
if (cfg["Database:Name"] is { Length: > 0 } dbName)
    connectionString = new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Database = dbName }.ConnectionString;

builder.Services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString, n => n.MigrationsHistoryTable(AppDb.MigrationsTable))
    .UseSnakeCaseNamingConvention());
builder.Services.AddScoped<ProfileStore>();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(cfg["DataProtection:KeysPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "keys")))
    .SetApplicationName("fbap-server");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "fbap.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.LoginPath = "/login.html";
        // API calls get 401 instead of a redirect to the login page.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = 401;
            else ctx.Response.Redirect("/login.html?next=" + Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString));
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var uiOrigins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[]
{
    "http://localhost:3000",
    "http://localhost:5173",
    "http://localhost:8080"
};

builder.Services.AddCors(o =>
{
    o.AddPolicy(DeviceEndpoints.CorsPolicy, p => p
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .WithHeaders("Content-Type", "X-Device-Key"));

    o.AddPolicy("UiPolicy", p => p
        .WithOrigins(uiOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await InitDatabase(app);

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();

// The web editor reuses the extension's own dashboard files.
var extPath = ExtensionPath(app);
var extFiles = new PhysicalFileProvider(extPath);
var extAllowed = new Regex(@"^/(dashboard\.(css|js)|lib/[a-z0-9-]+\.js|icons/[a-z0-9]+\.png)$");
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/app", out var rest) && extAllowed.IsMatch(rest.Value ?? ""),
    branch => branch.UseStaticFiles(new StaticFileOptions { FileProvider = extFiles, RequestPath = "/app" }));

app.UseCors("UiPolicy");
app.UseAuthentication();
app.UseAuthorization();

// dashboard.html + the shim that turns chrome.storage into server calls.
app.MapGet("/app/dashboard.html", async () =>
{
    var html = await File.ReadAllTextAsync(Path.Combine(extPath, "dashboard.html"));
    html = html.Replace("</head>", "  <link rel=\"stylesheet\" href=\"/web/shim.css\">\n</head>")
        .Replace("<script type=\"module\" src=\"dashboard.js\"></script>",
            "<script src=\"/web/shim.js\"></script>\n  <script type=\"module\" src=\"dashboard.js\"></script>");
    return Results.Content(html, "text/html; charset=utf-8");
}).RequireAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

AuthEndpoints.Map(app);
AdminEndpoints.Map(app);
DeviceEndpoints.Map(app);

app.Run();

// KEY=value lines of a .env file (current folder, then its parent: server/.env)
// become environment variables. Variables already set win.
static void LoadDotEnv()
{
    var dir = Directory.GetCurrentDirectory();
    var file = new[] { Path.Combine(dir, ".env"), Path.Combine(dir, "..", ".env") }.FirstOrDefault(File.Exists);
    if (file is null) return;
    foreach (var raw in File.ReadAllLines(file))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
        if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
    }
}

static string ExtensionPath(WebApplication app)
{
    var configured = app.Configuration["Extension:Path"];
    var candidates = new[]
    {
        configured,
        Path.Combine(app.Environment.ContentRootPath, "extension"), // published output
        Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "client")), // client alongside backend
        Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "client")), // source tree under backend/SIRI.AUTOPOST.Server
        Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..")), // legacy source tree
    };
    foreach (var c in candidates)
        if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "dashboard.html"))) return Path.GetFullPath(c);
    throw new InvalidOperationException("ไม่พบไฟล์ส่วนขยาย (dashboard.html) ตั้งค่า Extension:Path ให้ชี้ไปที่โฟลเดอร์ส่วนขยาย");
}

static async Task InitDatabase(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Init");
    await db.Database.MigrateAsync();
    if (await db.Users.AnyAsync()) return;

    var username = app.Configuration["Admin:Username"] is { Length: > 0 } u ? u : "admin";
    var password = app.Configuration["Admin:Password"];
    var generated = string.IsNullOrEmpty(password);
    if (generated) password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
    var user = new User { Username = username };
    user.PasswordHash = AuthEndpoints.HashPassword(user, password!);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    if (generated)
        log.LogWarning("สร้างผู้ดูแลระบบ {User} รหัสผ่าน {Password} (เปลี่ยนรหัสได้ในหน้าเว็บ)", username, password);
    else
        log.LogInformation("สร้างผู้ดูแลระบบ {User} จาก Admin:Password", username);
}
