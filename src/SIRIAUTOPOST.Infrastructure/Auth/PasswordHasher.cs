using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;
using IdentityHasher = Microsoft.AspNetCore.Identity.PasswordHasher<SIRIAUTOPOST.Domain.Entities.User>;
using IdentityResult = Microsoft.AspNetCore.Identity.PasswordVerificationResult;

namespace SIRIAUTOPOST.Infrastructure.Auth;

// ASP.NET Core Identity's PBKDF2 hasher (versioned format, so the algorithm can be upgraded later).
public sealed class PasswordHasher : IPasswordHasher
{
    private readonly IdentityHasher _inner = new();

    public string Hash(User user, string password) => _inner.HashPassword(user, password);

    public bool Verify(User user, string hash, string password) =>
        !string.IsNullOrEmpty(hash) && _inner.VerifyHashedPassword(user, hash, password) != IdentityResult.Failed;
}
