using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Application.Features.Orders;
using Ordering.Application.Features.Products;
using Ordering.Application.Idempotency;
using Ordering.Application.Notifications;
using Ordering.Infrastructure.Common;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Notifications;
using Ordering.Infrastructure.Outbox;
using Ordering.Infrastructure.Persistence;
using Ordering.Infrastructure.Read;
using Ordering.Infrastructure.Redis;

namespace Ordering.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Ordering";

    /// <summary>Must differ per running instance; see README.</summary>
    public const string WorkerIdKey = "Snowflake:WorkerId";

    /// <summary>When true the host migrates and seeds on startup (Development only by default).</summary>
    public const string InitializeOnStartupKey = "Database:InitializeOnStartup";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        var workerId = configuration.GetValue<int?>(WorkerIdKey) ?? 0;

        services.AddSingleton<IDbConnectionFactory>(new SqlConnectionFactory(connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator>(sp => new SnowflakeIdGenerator(sp.GetRequiredService<IClock>(), workerId));

        // snake_case columns map onto PascalCase read-model properties.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Interceptors registered by the host (tests: SQL statement log; Task 12: wait stats) ride along.
        services.AddDbContext<OrderingDbContext>((provider, options) =>
            options.UseSqlServer(connectionString).AddInterceptors(provider.GetServices<IInterceptor>()));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Write side: all share the scoped DbContext, hence the command's transaction.
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOutbox, TransactionalOutbox>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        // Read side: Dapper on its own connections, never inside a transaction.
        services.AddScoped<IProductQueryRepository, ProductQueryRepository>();
        services.AddScoped<IOrderQueryRepository, OrderQueryRepository>();

        return services
            .AddRedis(configuration)
            .AddMessaging(configuration, workerId);
    }

    /// <summary>
    /// Outbox relay, RabbitMQ and the notification consumer. Options are bound once here and
    /// registered as plain singletons; the hosted services are switched off by the test hosts
    /// that run without a broker.
    /// </summary>
    private static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration, int workerId)
    {
        var rabbitMq = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        var relay = configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
        var consumer = configuration.GetSection(ConsumerOptions.SectionName).Get<ConsumerOptions>() ?? new ConsumerOptions();
        var delivery = configuration.GetSection(DeliveryOptions.SectionName).Get<DeliveryOptions>() ?? new DeliveryOptions();

        services.AddSingleton(rabbitMq);
        services.AddSingleton(relay);
        services.AddSingleton(consumer);
        services.AddSingleton(delivery);

        // One connection per process; one channel per hosted service (publisher = the relay's, queue = the consumer's).
        services.AddSingleton(sp => new RabbitMqConnection(rabbitMq, $"ordering-api/{workerId}", sp.GetRequiredService<ILogger<RabbitMqConnection>>()));
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton<INotificationQueue, RabbitMqNotificationQueue>();

        // Change hints (Task 13): one fire-and-forget publisher per process, and the subscription
        // the API's listener consumes. Same object, two seams.
        services.AddSingleton<RabbitMqChangeHints>();
        services.AddSingleton<IChangeHintPublisher>(sp => sp.GetRequiredService<RabbitMqChangeHints>());
        services.AddSingleton<IChangeHintSubscription>(sp => sp.GetRequiredService<RabbitMqChangeHints>());

        // Outbox bookkeeping: single statements on their own connections, outside the CQRS pipeline.
        services.AddSingleton<OutboxStore>();
        services.AddScoped<INotificationDeliveryService, FakeDeliveryService>();

        if (relay.Enabled)
        {
            services.AddHostedService(sp => new OutboxRelay(
                sp.GetRequiredService<OutboxStore>(),
                sp.GetRequiredService<IEventPublisher>(),
                sp.GetRequiredService<IClock>(),
                relay,
                RelayInstanceId(workerId),
                sp.GetRequiredService<ILogger<OutboxRelay>>()));
        }

        if (consumer.Enabled)
        {
            services.AddHostedService<NotificationConsumer>();
        }

        return services;
    }

    /// <summary>What a claimed row's <c>claimed_by</c> says; unique per process, at most 64 characters.</summary>
    private static string RelayInstanceId(int workerId)
    {
        var id = $"{Environment.MachineName}/{workerId}/{Environment.ProcessId}";
        return id.Length <= OutboxMessage.ClaimedByMaxLength ? id : id[^OutboxMessage.ClaimedByMaxLength..];
    }
}
