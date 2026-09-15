using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Notifications;
using Ordering.Infrastructure.Outbox;
using Ordering.IntegrationTests.Persistence;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// The real host on the collection's Testcontainers SQL Server. Development settings apply, so
/// the host migrates and seeds on startup (both idempotent against the fixture's database).
/// <see cref="Sql"/> sees every statement the host's EF context runs. The outbox relay and the
/// notification consumer are off: this host has no broker, and the Task 1–6 tests assert on
/// outbox rows the relay would otherwise claim.
/// </summary>
// Task 8: add RabbitMq:* overrides from a RabbitMqFixture, switch the relay/consumer on, and ResetAsync here.
public sealed class OrderingApiFactory(SqlServerFixture database) : WebApplicationFactory<Program>
{
    public SqlStatementLog Sql { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{Ordering.Infrastructure.DependencyInjection.ConnectionStringName}", database.ConnectionString);
        builder.UseSetting(Ordering.Infrastructure.DependencyInjection.WorkerIdKey, "7");
        builder.UseSetting($"{RelayOptions.SectionName}:{nameof(RelayOptions.Enabled)}", "false");
        builder.UseSetting($"{ConsumerOptions.SectionName}:{nameof(ConsumerOptions.Enabled)}", "false");

        builder.ConfigureServices(services => services.AddSingleton<IInterceptor>(Sql));
    }
}
