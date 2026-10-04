using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Api.Controllers;

public static class MediaResponse
{
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
