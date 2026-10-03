using FluentValidation;
using NSubstitute;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Application.Validators;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Tests;

public class AuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 3, 30, 0, TimeSpan.Zero);
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();

    public AuthTests() => _tokens.Create(Arg.Any<User>()).Returns(new AuthToken("jwt", Now.AddDays(3)));

    [Fact]
    public async Task Sign_up_refuses_a_registered_email()
    {
        _users.GetByEmailAsync("a@shop.co", Arg.Any<CancellationToken>())
            .Returns(User.Create("a@shop.co", "", UserRole.User, PlanKey.Free, Now));
        var handler = new SignUpCommandHandler(_users, Substitute.For<IWorkspaceRepository>(), Substitute.For<IMemberRepository>(), Substitute.For<IWorkspaceSeeder>(),
            _hasher, _tokens, Substitute.For<IUnitOfWork>(), new FixedClock(Now));

        await Assert.ThrowsAsync<ConflictException>(() => handler.HandleAsync(new SignUpCommand("A@shop.co", "password1", null, null)));
    }

    [Fact]
    public async Task Sign_up_creates_a_seeded_workspace_on_the_chosen_plan()
    {
        var workspaces = Substitute.For<IWorkspaceRepository>();
        var seeder = Substitute.For<IWorkspaceSeeder>();
        var handler = new SignUpCommandHandler(_users, workspaces, Substitute.For<IMemberRepository>(), seeder, _hasher, _tokens, Substitute.For<IUnitOfWork>(), new FixedClock(Now));

        var result = await handler.HandleAsync(new SignUpCommand("new@shop.co", "password1", "Nattaya", PlanKey.Pro));

        Assert.Equal(PlanKey.Pro, result.User.Plan);
        Assert.Equal(UserRole.User, result.User.Role);
        workspaces.Received(1).Add(Arg.Any<Workspace>());
        await seeder.Received(1).SeedAsync(Arg.Any<Workspace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Log_in_gives_the_same_error_for_unknown_email_and_wrong_password()
    {
        var user = User.Create("a@shop.co", "", UserRole.User, PlanKey.Free, Now);
        _users.GetByEmailAsync("a@shop.co", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify(user, Arg.Any<string>(), "wrong").Returns(false);
        var handler = new LogInCommandHandler(_users, _hasher, _tokens, Substitute.For<IUnitOfWork>(), new FixedClock(Now));

        var wrong = await Assert.ThrowsAsync<AuthenticationException>(() => handler.HandleAsync(new LogInCommand("a@shop.co", "wrong")));
        var unknown = await Assert.ThrowsAsync<AuthenticationException>(() => handler.HandleAsync(new LogInCommand("b@shop.co", "x")));
        Assert.Equal(wrong.Message, unknown.Message);
    }

    [Fact]
    public async Task Short_passwords_never_reach_the_handler()
    {
        var inner = Substitute.For<ICommandHandler<SignUpCommand, AuthResultDto>>();
        var decorator = new ValidationCommandHandlerDecorator<SignUpCommand, AuthResultDto>(inner, [new SignUpCommandValidator()]);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => decorator.HandleAsync(new SignUpCommand("not-an-email", "short", null, null)));

        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(SignUpCommand.Email));
        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(SignUpCommand.Password));
        await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }
}
