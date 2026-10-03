using FluentValidation;
using NSubstitute;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Posts.Commands;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Application.Validators;

namespace SIRIAUTOPOST.Application.Tests;

public class ValidationCommandHandlerDecoratorTests
{
    [Fact]
    public async Task Invalid_command_never_reaches_the_handler()
    {
        var inner = Substitute.For<ICommandHandler<CreatePostCommand, PostDto>>();
        var decorator = new ValidationCommandHandlerDecorator<CreatePostCommand, PostDto>(
            inner, [new CreatePostCommandValidator()]);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => decorator.HandleAsync(new CreatePostCommand("", "", null)));

        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(CreatePostCommand.Content));
        Assert.Contains(ex.Errors, e => e.PropertyName == nameof(CreatePostCommand.GroupUrl));
        await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }
}
