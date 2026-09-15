using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ordering.IntegrationTests.Persistence;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// The real host on the collection's Testcontainers SQL Server. Development settings apply, so
/// the host migrates and seeds on startup (both idempotent against the fixture's database).
/// <see cref="Sql"/> sees every statement the host's EF context runs.
/// </summary>
// Task 8: add RabbitMq:* overrides, the Notifications:Relay/Consumer switches and ResetAsync here.
public sealed class OrderingApiFactory(SqlServerFixture database) : WebApplicationFactory<Program>
{
    public SqlStatementLog Sql { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{Ordering.Infrastructure.DependencyInjection.ConnectionStringName}", database.ConnectionString);
        builder.UseSetting(Ordering.Infrastructure.DependencyInjection.WorkerIdKey, "7");

        builder.ConfigureServices(services => services.AddSingleton<IInterceptor>(Sql));
    }
}
