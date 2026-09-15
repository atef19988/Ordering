using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Abstractions.Behaviors;

namespace Ordering.Application.Abstractions.Messaging;

/// <summary>
/// Resolves the handler for a request from the current scope and runs it through the registered
/// <see cref="IPipelineBehavior{TRequest,TResponse}"/>s in registration order (first = outermost).
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Executors = new();

    public Task<Result> Send(ICommand command, CancellationToken cancellationToken = default) =>
        Execute(command, cancellationToken);

    public Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) =>
        Execute(command, cancellationToken);

    public Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
        Execute(query, cancellationToken);

    private Task<TResponse> Execute<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
        where TResponse : Result, IResultFactory<TResponse>
    {
        var executor = (Executor<TResponse>)Executors.GetOrAdd(
            request.GetType(),
            static (requestType, responseType) =>
                Activator.CreateInstance(typeof(Executor<,>).MakeGenericType(requestType, responseType))!,
            typeof(TResponse));

        return executor.Execute(request, services, cancellationToken);
    }

    private abstract class Executor<TResponse>
        where TResponse : Result, IResultFactory<TResponse>
    {
        public abstract Task<TResponse> Execute(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class Executor<TRequest, TResponse> : Executor<TResponse>
        where TRequest : IRequest<TResponse>
        where TResponse : Result, IResultFactory<TResponse>
    {
        public override Task<TResponse> Execute(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken)
        {
            var typedRequest = (TRequest)request;
            var handler = services.GetRequiredService<IRequestHandler<TRequest, TResponse>>();

            Func<Task<TResponse>> pipeline = () => handler.Handle(typedRequest, cancellationToken);

            foreach (var behavior in services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
            {
                var next = pipeline;
                pipeline = () => behavior.Handle(typedRequest, next, cancellationToken);
            }

            return pipeline();
        }
    }
}
