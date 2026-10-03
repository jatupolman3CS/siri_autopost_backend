using System.Text.Json;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Api.Services;

/// <summary>
/// Where the extension's files are on this server and which version they are. The folder is <c>Extension:Path</c>,
/// <c>./extension</c> next to the app (Docker) or <c>client/</c> above the content root (dev).
/// </summary>
public sealed class ExtensionPackage(IConfiguration config, IWebHostEnvironment env) : IExtensionPackage
{
    public string? Directory
    {
        get
        {
            var configured = config["Extension:Path"];
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "manifest.json")))
                return configured;
            var bundled = Path.Combine(AppContext.BaseDirectory, "extension");
            if (File.Exists(Path.Combine(bundled, "manifest.json"))) return bundled;
            for (var d = new DirectoryInfo(env.ContentRootPath); d is not null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "client");
                if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
            }
            return null;
        }
    }

    public string? Version => Directory is { } dir ? ReadVersion(Path.Combine(dir, "manifest.json")) : null;

    public static string? ReadVersion(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch
        {
            return null;
        }
    }
}
