using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Application.Features.Orders;
using Ordering.Application.Features.Products;
using Ordering.Infrastructure.Common;
using Ordering.Infrastructure.Outbox;
using Ordering.Infrastructure.Persistence;
using Ordering.Infrastructure.Read;

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

        services.AddDbContext<OrderingDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Write side: all share the scoped DbContext, hence the command's transaction.
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOutbox, TransactionalOutbox>();

        // Read side: Dapper on its own connections, never inside a transaction.
        services.AddScoped<IProductQueryRepository, ProductQueryRepository>();
        services.AddScoped<IOrderQueryRepository, OrderQueryRepository>();

        return services;
    }
}
