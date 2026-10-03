using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Api.Services;

namespace SIRIAUTOPOST.Api.Controllers;

/// <summary>
/// Hands out the Chrome extension as a zip, so a person can install it from the web app instead of
/// getting the files from someone. Only the runtime files go in (never <c>client/config</c>, which can
/// hold a person's own backup, nor the dev tools).
/// </summary>
[ApiController]
[Route("api/extension")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ExtensionDownloadController(ExtensionPackage package) : ControllerBase
{
    private static readonly string[] RootFiles =
        ["manifest.json", "background.js", "content.js", "status.html", "status.css", "status.js"];

    private static readonly string[] Folders = ["icons", "lib"];

    private static readonly object Gate = new();
    private static (string Key, byte[] Zip)? _cached;

    [HttpGet("download")]
    public IActionResult Download()
    {
        var dir = package.Directory;
        if (dir is null) return Problem("ไม่พบไฟล์ส่วนขยายบนเซิร์ฟเวอร์", statusCode: StatusCodes.Status404NotFound);
        var zip = BuildZip(dir, out var version);
        return File(zip, "application/zip", $"autopost-extension-{version}.zip");
    }

    private static byte[] BuildZip(string dir, out string version)
    {
        var files = RootFiles.Select(f => Path.Combine(dir, f)).Where(System.IO.File.Exists)
            .Concat(Folders.Select(f => Path.Combine(dir, f)).Where(Directory.Exists)
                .SelectMany(f => Directory.GetFiles(f, "*", SearchOption.AllDirectories)))
            .Order(StringComparer.Ordinal).ToList();
        var key = string.Join('|', files.Select(f => $"{f}:{System.IO.File.GetLastWriteTimeUtc(f).Ticks}"));
        version = ExtensionPackage.ReadVersion(Path.Combine(dir, "manifest.json")) ?? "latest";
        lock (Gate)
        {
            if (_cached is { } c && c.Key == key) return c.Zip;
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var f in files)
                {
                    var name = "autopost-extension/" + Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/');
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var src = System.IO.File.OpenRead(f);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
            _cached = (key, ms.ToArray());
            return _cached.Value.Zip;
        }
    }
}
