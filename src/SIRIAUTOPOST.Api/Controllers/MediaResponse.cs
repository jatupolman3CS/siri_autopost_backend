using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;

namespace SIRIAUTOPOST.Api.Controllers;

public static class MediaResponse
{
    /// <summary>
    /// The file as a response. Files in object storage are streamed through the API (not redirected), so the
    /// caller's auth header never leaves the API and the storage needs no CORS rule for the web app.
    /// </summary>
    public static async Task<IActionResult> ToResultAsync(this ControllerBase c, MediaContent m, IHttpClientFactory http, CancellationToken ct)
    {
        if (m.ExternalUrl is null) return c.File(m.Data, m.ContentType, m.Name);
        var resp = await http.CreateClient().GetAsync(m.ExternalUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        c.HttpContext.Response.RegisterForDispose(resp);
        if (!resp.IsSuccessStatusCode) return c.StatusCode(StatusCodes.Status502BadGateway);
        return c.File(await resp.Content.ReadAsStreamAsync(ct), m.ContentType, m.Name);
    }
}
