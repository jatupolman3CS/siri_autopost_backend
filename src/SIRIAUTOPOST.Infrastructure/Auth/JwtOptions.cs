namespace SIRIAUTOPOST.Infrastructure.Auth;

// Bound from the "Jwt" configuration section.
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "siriautopost";
    public string Audience { get; set; } = "siriautopost-web";
    /// <summary>HMAC-SHA256 signing key, at least 32 characters. Set it via configuration or environment.</summary>
    public string Key { get; set; } = "";
    public int LifetimeHours { get; set; } = 72;
}
