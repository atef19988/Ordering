using Microsoft.OpenApi;
using Ordering.Api.Health;
using Ordering.Api.Logging;
using Ordering.Api.Middleware;
using Ordering.Api.Serialization;
using Ordering.Application;
using Ordering.Infrastructure;
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
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");

// Task 3: var api = app.MapGroup("/api"); api.MapProducts(); api.MapOrders();

app.Run();

public partial class Program;
