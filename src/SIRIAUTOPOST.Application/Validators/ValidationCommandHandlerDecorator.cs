using FluentValidation;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Application.Validators;

// Runs every IValidator<TCommand> before the real handler. Throws ValidationException (API: 400).
public sealed class ValidationCommandHandlerDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var v in validators)
            failures.AddRange((await v.ValidateAsync(command, ct)).Errors);
        if (failures.Count > 0) throw new ValidationException(failures);
        return await inner.HandleAsync(command, ct);
    }
}
