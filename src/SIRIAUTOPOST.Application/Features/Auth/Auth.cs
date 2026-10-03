using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Auth;

public sealed record SignUpCommand(string Email, string Password, string? Name, PlanKey? Plan) : ICommand<AuthResultDto>;

/// <summary>New shop user with one workspace, filled with the sample accounts.</summary>
public sealed class SignUpCommandHandler(
    IUserRepository users, IWorkspaceRepository workspaces, IWorkspaceSeeder seeder,
    IPasswordHasher hasher, ITokenService tokens, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SignUpCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(SignUpCommand c, CancellationToken ct = default)
    {
        var email = User.NormalizeEmail(c.Email);
        if (await users.GetByEmailAsync(email, ct) is not null) throw new ConflictException("อีเมลนี้มีบัญชีอยู่แล้ว");
        var now = clock.GetUtcNow();
        var user = User.Create(email, c.Name ?? "", UserRole.User, c.Plan ?? PlanKey.Free, now);
        user.SetPasswordHash(hasher.Hash(user, c.Password));
        users.Add(user);
        var ws = Workspace.Create(user.Id, $"เวิร์กสเปซของ {user.Name}", now);
        workspaces.Add(ws);
        await seeder.SeedAsync(ws, ct);
        await uow.SaveChangesAsync(ct);
        var token = tokens.Create(user);
        return new AuthResultDto(token.Token, token.ExpiresAt, UserDto.From(user));
    }
}

public sealed record LogInCommand(string Email, string Password) : ICommand<AuthResultDto>;

public sealed class LogInCommandHandler(IUserRepository users, IPasswordHasher hasher, ITokenService tokens)
    : ICommandHandler<LogInCommand, AuthResultDto>
{
    public async Task<AuthResultDto> HandleAsync(LogInCommand c, CancellationToken ct = default)
    {
        var user = await users.GetByEmailAsync(User.NormalizeEmail(c.Email), ct);
        // Same message for unknown email and wrong password.
        if (user is null || !hasher.Verify(user, user.PasswordHash, c.Password))
            throw new AuthenticationException("อีเมลหรือรหัสผ่านไม่ถูกต้อง");
        var token = tokens.Create(user);
        return new AuthResultDto(token.Token, token.ExpiresAt, UserDto.From(user));
    }
}

public sealed record GetMeQuery : IQuery<UserDto>;

public sealed class GetMeQueryHandler(IUserRepository users, ICurrentUser current) : IQueryHandler<GetMeQuery, UserDto>
{
    public async Task<UserDto> HandleAsync(GetMeQuery q, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        return UserDto.From(user);
    }
}

/// <summary>Self-service plan change. Billing (card charge, proration) is not wired to a payment provider yet.</summary>
public sealed record ChangePlanCommand(PlanKey Plan) : ICommand<UserDto>;

public sealed class ChangePlanCommandHandler(IUserRepository users, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<ChangePlanCommand, UserDto>
{
    public async Task<UserDto> HandleAsync(ChangePlanCommand c, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(current.UserId, ct) ?? throw new AuthenticationException("ต้องเข้าสู่ระบบใหม่");
        user.ChangePlan(c.Plan);
        await uow.SaveChangesAsync(ct);
        return UserDto.From(user);
    }
}
