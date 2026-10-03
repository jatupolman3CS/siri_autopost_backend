using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Infrastructure.Auth;

// Verifies Google Identity Services ID tokens through Google's tokeninfo endpoint (it checks the signature and expiry);
// we check the audience (our client id) and the verified email. Config: "Google:ClientId".
public sealed class GoogleTokenVerifier(HttpClient http, IConfiguration config) : IGoogleTokenVerifier
{
    private static readonly string[] Issuers = ["accounts.google.com", "https://accounts.google.com"];

    private sealed record TokenInfo(
        [property: JsonPropertyName("aud")] string? Aud,
        [property: JsonPropertyName("iss")] string? Iss,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] string? EmailVerified,
        [property: JsonPropertyName("name")] string? Name);

    public string? ClientId => string.IsNullOrWhiteSpace(config["Google:ClientId"]) ? null : config["Google:ClientId"]!.Trim();

    public async Task<GoogleIdentity> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        const string invalid = "เข้าสู่ระบบด้วย Google ไม่สำเร็จ";
        if (ClientId is null) throw new AuthenticationException("ยังไม่ได้ตั้งค่าการเข้าสู่ระบบด้วย Google");
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 4096) throw new AuthenticationException(invalid);
        TokenInfo? info;
        try
        {
            using var response = await http.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}", ct);
            if (!response.IsSuccessStatusCode) throw new AuthenticationException(invalid);
            info = await response.Content.ReadFromJsonAsync<TokenInfo>(ct);
        }
        catch (HttpRequestException) { throw new AuthenticationException(invalid); }
        if (info is null || info.Aud != ClientId || !Issuers.Contains(info.Iss) ||
            string.IsNullOrWhiteSpace(info.Email) || info.EmailVerified != "true")
            throw new AuthenticationException(invalid);
        return new GoogleIdentity(info.Email, info.Name);
    }
}
