using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Api.Controllers;

public static class MediaResponse
{
    private static readonly string[] Mpeg4Types = ["video/mp4", "video/quicktime", "video/x-m4v"];

    /// <summary>
    /// The file for a posting browser. An MP4 whose index sits after its picture data, or that carries a cover picture,
    /// never finished processing in Facebook's composer, so a video like that goes out in the layout Facebook takes
    /// (<see cref="Mp4Layout"/>: lossless, the library file itself is not touched). It is read into memory for that; library
    /// files are 100 MB at most. Every other file is sent as <see cref="ToResultAsync"/> does.
    /// </summary>
    public static async Task<IActionResult> ToDeviceResultAsync(this ControllerBase c, MediaContent m, IHttpClientFactory http, IObjectStorage storage, CancellationToken ct)
    {
        var result = await c.ToResultAsync(m, http, storage, ct);
        if (!Mpeg4Types.Contains(m.ContentType, StringComparer.OrdinalIgnoreCase)) return result;
        byte[] bytes;
        switch (result)
        {
            case FileContentResult content:
                bytes = content.FileContents;
                break;
            case FileStreamResult stream:
                await using (stream.FileStream)
                {
                    var buffer = new MemoryStream();
                    await stream.FileStream.CopyToAsync(buffer, ct);
                    bytes = buffer.ToArray();
                }
                break;
            default:
                return result; // a 404 or 502
        }
        return c.File(Mp4Layout.ForFacebook(bytes) ?? bytes, m.ContentType, m.Name);
    }

    /// <summary>
    /// The file as a response. Files in object storage are streamed through the API (not redirected), so the
    /// bucket stays private and needs no CORS rule. <c>r2://key</c> addresses are read with the bucket's keys;
    /// any other address is fetched over HTTP.
    /// </summary>
    public static async Task<IActionResult> ToResultAsync(this ControllerBase c, MediaContent m, IHttpClientFactory http, IObjectStorage storage, CancellationToken ct)
    {
        if (m.ExternalUrl is null) return c.File(m.Data, m.ContentType, m.Name);
        if (m.ExternalUrl.StartsWith("r2://", StringComparison.Ordinal))
        {
            var s = await storage.OpenReadAsync(m.ExternalUrl[5..], ct);
            return s is null ? c.NotFound() : c.File(s, m.ContentType, m.Name);
        }
        var resp = await http.CreateClient().GetAsync(m.ExternalUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        c.HttpContext.Response.RegisterForDispose(resp);
        if (!resp.IsSuccessStatusCode) return c.StatusCode(StatusCodes.Status502BadGateway);
        return c.File(await resp.Content.ReadAsStreamAsync(ct), m.ContentType, m.Name);
    }
}
