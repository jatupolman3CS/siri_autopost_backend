using System.Security.Claims;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Infrastructure.Auth;

namespace SIRIAUTOPOST.Api.Auth;

// Reads the user id from the JWT "sub" claim of the current request.
public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    public Guid UserId
    {
        get
        {
            var sub = http.HttpContext?.User.FindFirstValue("sub") ?? http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        }
    }

    public Guid? ImpersonatorId =>
        Guid.TryParse(http.HttpContext?.User.FindFirstValue(JwtTokenService.ActorClaim), out var id) ? id : null;
}
