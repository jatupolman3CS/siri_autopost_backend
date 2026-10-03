namespace SIRIAUTOPOST.Application.Interfaces.Messaging;

// Minimal CQRS contracts. Commands change state, queries only read.
// Controllers depend on ICommandHandler/IQueryHandler directly; no mediator library.
public interface ICommand<TResult>;

public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}

// Result of a command that returns nothing.
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}
