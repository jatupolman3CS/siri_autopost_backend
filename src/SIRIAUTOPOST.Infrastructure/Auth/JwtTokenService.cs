using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Infrastructure.Auth;

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    /// <summary>The admin acting as the user (RFC 8693 uses "act" for the acting party).</summary>
    public const string ActorClaim = "act";

    public AuthToken Create(User user) => Build(user, TimeSpan.FromHours(options.Value.LifetimeHours), null);

    public AuthToken CreateImpersonation(User user, Guid adminId, TimeSpan lifetime) => Build(user, lifetime, adminId);

    private AuthToken Build(User user, TimeSpan lifetime, Guid? actor)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.Add(lifetime);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("role", user.Role.ToString().ToLowerInvariant()),
        };
        if (actor is { } a) claims.Add(new Claim(ActorClaim, a.ToString()));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Expires = expires.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(SigningKey(o), SecurityAlgorithms.HmacSha256),
        });
        return new AuthToken(token, expires);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions o)
    {
        if (o.Key.Length < 32) throw new InvalidOperationException("Jwt:Key ต้องยาวอย่างน้อย 32 ตัวอักษร");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key));
    }
}
