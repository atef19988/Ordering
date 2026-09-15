using FluentValidation;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Abstractions.Behaviors;

/// <summary>
/// Runs every <see cref="IValidator{T}"/> registered for the request and short-circuits with a
/// <see cref="ValidationError"/> before the handler (and any transaction) is reached.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result, IResultFactory<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var errors = results
            .SelectMany(r => r.Errors)
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray(), StringComparer.Ordinal);

        return errors.Count == 0
            ? await next()
            : TResponse.Failure(new ValidationError(errors));
    }
}
