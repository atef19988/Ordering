using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ordering.Application.Abstractions;
using Ordering.Application.Notifications;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Notifications;
using Ordering.Infrastructure.Outbox;
using Ordering.IntegrationTests.Backend;
using Serilog.Core;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// The real host on the collection's SQL Server and RabbitMQ. Development settings apply, so the
/// host migrates and seeds on startup (both idempotent). One instance is one process: it has its
/// own <see cref="WorkerId"/>, its own <see cref="Failures"/>, its own <see cref="Deliveries"/>
/// and <see cref="Logs"/>, so a test can run two of them against the same backend and tell
/// their side effects apart. Init-only properties must be set before the first
/// <c>CreateClient()</c>; that is when the host is built.
/// </summary>
/// <remarks>
/// <see cref="Messaging"/> is off by default: the relay and consumer are hosted services that
/// would race the tests which assert on outbox rows, and most tests need neither. Notification
/// tests turn it on per host.
/// </remarks>
public sealed class OrderingApiFactory(BackendFixture backend) : WebApplicationFactory<Program>
{
    public SqlStatementLog Sql { get; } = new();

    public InjectedFailures Failures { get; } = new();

    public DeliveryLog Deliveries { get; } = new();

    public CapturedLogs Logs { get; } = new();

    /// <summary>Distinct per host in a test, so two hosts on one database never mint the same id.</summary>
    public int WorkerId { get; init; } = 7;

    /// <summary>Runs the outbox relay and the notification consumer in this host.</summary>
    public bool Messaging { get; init; }

    /// <summary>Extra configuration, e.g. <c>Notifications:Delivery:FailureMode</c>; applied last, so it overrides anything above.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{Ordering.Infrastructure.DependencyInjection.ConnectionStringName}", backend.Sql.ConnectionString);
        builder.UseSetting(Ordering.Infrastructure.DependencyInjection.WorkerIdKey, WorkerId.ToString());

        var rabbit = backend.RabbitMq.Options;
        builder.UseSetting($"{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Host)}", rabbit.Host);
        builder.UseSetting($"{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Port)}", rabbit.Port.ToString());
        builder.UseSetting($"{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.User)}", rabbit.User);
        builder.UseSetting($"{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Password)}", rabbit.Password);

        builder.UseSetting($"{RelayOptions.SectionName}:{nameof(RelayOptions.Enabled)}", Messaging.ToString());
        builder.UseSetting($"{ConsumerOptions.SectionName}:{nameof(ConsumerOptions.Enabled)}", Messaging.ToString());

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInterceptor>(Sql);
            services.AddSingleton<ILogEventSink>(Logs);

            services.RemoveAll<IFailurePoint>();
            services.AddSingleton<IFailurePoint>(Failures);

            // The real fake still decides success/failure; the recorder only watches.
            services.RemoveAll<INotificationDeliveryService>();
            services.AddScoped<FakeDeliveryService>();
            services.AddScoped<INotificationDeliveryService>(sp => new RecordingDeliveryService(sp.GetRequiredService<FakeDeliveryService>(), Deliveries));
        });
    }
}
