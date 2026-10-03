using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Posts.Commands;
using SIRIAUTOPOST.Application.Features.Posts.Queries;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Application.Validators;

namespace SIRIAUTOPOST.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        // Posts. Register every new command/query of a feature here.
        services.AddCommand<CreatePostCommand, PostDto, CreatePostCommandHandler>();
        services.AddCommand<UpdatePostCommand, PostDto, UpdatePostCommandHandler>();
        services.AddCommand<DeletePostCommand, Unit, DeletePostCommandHandler>();
        services.AddQuery<GetPostByIdQuery, PostDto, GetPostByIdQueryHandler>();
        services.AddQuery<GetPostsQuery, IReadOnlyList<PostDto>, GetPostsQueryHandler>();

        return services;
    }

    // The handler is wrapped so its validators always run first.
    private static void AddCommand<TCommand, TResult, THandler>(this IServiceCollection services)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        services.AddScoped<THandler>();
        services.AddScoped<ICommandHandler<TCommand, TResult>>(sp =>
            new ValidationCommandHandlerDecorator<TCommand, TResult>(
                sp.GetRequiredService<THandler>(), sp.GetServices<IValidator<TCommand>>()));
    }

    private static void AddQuery<TQuery, TResult, THandler>(this IServiceCollection services)
        where TQuery : IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult> =>
        services.AddScoped<IQueryHandler<TQuery, TResult>, THandler>();
}
