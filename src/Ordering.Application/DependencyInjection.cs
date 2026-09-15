using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Abstractions.Behaviors;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<IDispatcher, Dispatcher>();

        // Registration order is pipeline order: outermost first.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        services.AddRequestHandlers(assembly);

        return services;
    }

    /// <summary>
    /// Registers every concrete <see cref="IRequestHandler{TRequest,TResponse}"/> in
    /// <paramref name="assembly"/> under each closed handler interface it implements.
    /// </summary>
    public static IServiceCollection AddRequestHandlers(this IServiceCollection services, Assembly assembly)
    {
        var registrations =
            from type in assembly.GetTypes()
            where type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            from contract in type.GetInterfaces()
            where contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
            select (Contract: contract, Implementation: type);

        foreach (var (contract, implementation) in registrations)
        {
            services.AddScoped(contract, implementation);
        }

        return services;
    }
}
