using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Auth;

public sealed record SignUpCommand(string Email, string Password, string? Name) : ICommand<AuthResultDto>;

/// <summary>New shop user on the Free plan with one workspace, filled with the sample accounts. A paid plan is bought at Stripe Checkout afterwards.</summary>
public sealed class SignUpCommandHandler(
    IUserRepository users, IWorkspaceRepository workspaces, IMemberRepository members, IWorkspaceSeeder seeder,
    IPasswordHasher hasher, ITokenService tokens, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SignUpCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(SignUpCommand c, CancellationToken ct = default)
    {
        var email = User.NormalizeEmail(c.Email);
        if (await users.GetByEmailAsync(email, ct) is not null) throw new ConflictException("อีเมลนี้มีบัญชีอยู่แล้ว");
        var now = clock.GetUtcNow();
        var user = User.Create(email, c.Name ?? "", UserRole.User, PlanKey.Free, now);
        user.SetPasswordHash(hasher.Hash(user, c.Password));
        users.Add(user);
        var ws = Workspace.Create(user.Id, Workspace.DefaultNameFor(user.Name), now);
        workspaces.Add(ws);
        await seeder.SeedAsync(ws, ct);
        // Invitations sent to this email before the account existed.
        foreach (var m in await members.ListPendingAsync(email, ct)) m.Join(user.Id, now);
        await uow.SaveChangesAsync(ct);
        var token = tokens.Create(user);
        return new AuthResultDto(token.Token, token.ExpiresAt, UserDto.From(user));
    }
}

public sealed record LogInCommand(string Email, string Password) : ICommand<AuthResultDto>;

public sealed class LogInCommandHandler(
    IUserRepository users, IPasswordHasher hasher, ITokenService tokens, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<LogInCommand, AuthResultDto>
{
    public const string BlockedMessage = "บัญชีนี้ถูกระงับการใช้งาน ติดต่อผู้ดูแลแพลตฟอร์ม";

    public async Task<AuthResultDto> HandleAsync(LogInCommand c, CancellationToken ct = default)
    {
        var user = await users.GetByEmailAsync(User.NormalizeEmail(c.Email), ct);
        // Same message for unknown email and wrong password.
        if (user is null || !hasher.Verify(user, user.PasswordHash, c.Password))
            throw new AuthenticationException("อีเมลหรือรหัสผ่านไม่ถูกต้อง");
        // 403, not 401, so the sign-in page can tell a suspended account from a wrong password.
        if (user.IsBlocked) throw new ForbiddenException(BlockedMessage);
        user.Seen(clock.GetUtcNow());
        await uow.SaveChangesAsync(ct);
        var token = tokens.Create(user);
        return new AuthResultDto(token.Token, token.ExpiresAt, UserDto.From(user));
    }
}

public sealed record GoogleLogInCommand(string IdToken) : ICommand<AuthResultDto>;

/// <summary>Signs in with a Google ID token; an unknown (verified) email gets a new shop account without a password.</summary>
public sealed class GoogleLogInCommandHandler(
    IGoogleTokenVerifier google, IUserRepository users, IWorkspaceRepository workspaces, IMemberRepository members,
    IWorkspaceSeeder seeder, ITokenService tokens, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<GoogleLogInCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(GoogleLogInCommand c, CancellationToken ct = default)
    {
        var identity = await google.VerifyAsync(c.IdToken, ct);
        var email = User.NormalizeEmail(identity.Email);
        var now = clock.GetUtcNow();
        var user = await users.GetByEmailAsync(email, ct);
        if (user is null)
        {
            user = User.Create(email, identity.Name ?? "", UserRole.User, PlanKey.Free, now);
            users.Add(user); // no password hash: password login stays impossible until one is set
            var ws = Workspace.Create(user.Id, Workspace.DefaultNameFor(user.Name), now);
            workspaces.Add(ws);
            await seeder.SeedAsync(ws, ct);
            foreach (var m in await members.ListPendingAsync(email, ct)) m.Join(user.Id, now);
        }
        else if (user.IsBlocked) throw new ForbiddenException(LogInCommandHandler.BlockedMessage);
        else user.Seen(now);
        await uow.SaveChangesAsync(ct);
        var token = tokens.Create(user);
        return new AuthResultDto(token.Token, token.ExpiresAt, UserDto.From(user));
    }
}

public sealed record GetMeQuery : IQuery<UserDto>;

public sealed class GetMeQueryHandler(IUserRepository users, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : IQueryHandler<GetMeQuery, UserDto>
{
    public async Task<UserDto> HandleAsync(GetMeQuery q, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        if (current.ImpersonatorId is null) // "last active" for the team and admin pages
        {
            user.Seen(clock.GetUtcNow());
            await uow.SaveChangesAsync(ct);
        }
        return UserDto.From(user);
    }
}
