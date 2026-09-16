using Microsoft.OpenApi;
using Ordering.Api.Endpoints;
using Ordering.Api.Health;
using Ordering.Api.Logging;
using Ordering.Api.Middleware;
using Ordering.Application;
using Ordering.Application.Abstractions.Serialization;
using Ordering.Infrastructure;
using Ordering.Infrastructure.Persistence;
using Serilog;
using Serilog.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ILogEventEnricher, HttpContextEnricher>();

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new LongAsStringJsonConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.MapType<long>(() => new OpenApiSchema { Type = JsonSchemaType.String, Format = "int64" }));
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// `dotnet run -- seed [--products N]`: migrate + seed (+ the load catalogue), then exit without serving.
if (args.Contains("seed", StringComparer.OrdinalIgnoreCase))
{
    await DbInitializer.InitializeAsync(app.Services, CancellationToken.None);

    if (LoadProductsArg(args) is { } products)
    {
        await DbInitializer.SeedLoadCatalogueAsync(app.Services, products, CancellationToken.None);
    }

    return;
}

app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");

var api = app.MapGroup("/api");
api.MapProducts();
api.MapOrders();

if (app.Configuration.GetValue<bool>(Ordering.Infrastructure.DependencyInjection.InitializeOnStartupKey))
{
    await DbInitializer.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);
}

app.Run();

/// <summary>The value after <c>--products</c>, or null when the switch is absent.</summary>
static int? LoadProductsArg(string[] args)
{
    var index = Array.FindIndex(args, a => string.Equals(a, "--products", StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        return null;
    }

    return index + 1 < args.Length && int.TryParse(args[index + 1], out var count) && count >= 0
        ? count
        : throw new ArgumentException("--products must be followed by a non-negative integer, e.g. `seed --products 100000`.");
}

public partial class Program;
