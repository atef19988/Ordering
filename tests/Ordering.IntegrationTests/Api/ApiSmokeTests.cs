using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ordering.Infrastructure.Notifications;
using Ordering.Infrastructure.Outbox;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// Proves the host composes: DI graph valid, middleware, JSON contract and Swagger wired. Needs no
/// database and no broker, so startup migration/seeding (on in Development), the outbox relay and
/// the notification consumer are switched off here. Kept off the containers on purpose: these
/// run in milliseconds; everything that touches the backend lives in the Backend collection.
/// </summary>
public class ApiSmokeTests(ApiSmokeTests.HostWithoutDatabaseInitialization factory) : IClassFixture<ApiSmokeTests.HostWithoutDatabaseInitialization>
{
    public sealed class HostWithoutDatabaseInitialization : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(Ordering.Infrastructure.DependencyInjection.InitializeOnStartupKey, "false");
            builder.UseSetting($"{RelayOptions.SectionName}:{nameof(RelayOptions.Enabled)}", "false");
            builder.UseSetting($"{ConsumerOptions.SectionName}:{nameof(ConsumerOptions.Enabled)}", "false");
        }
    }

    [Fact]
    public async Task Swagger_document_is_served()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"openapi\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_route_is_404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/nothing-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Snowflake_ids_cross_the_wire_as_strings_and_are_accepted_back_either_way()
    {
        var options = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        const long id = 7_336_899_183_492_280_321; // above 2^53: a JS number would lose the low digits

        var json = JsonSerializer.Serialize(new IdEnvelope(id), options);

        Assert.Equal("{\"id\":\"7336899183492280321\"}", json);
        Assert.Equal(id, JsonSerializer.Deserialize<IdEnvelope>(json, options)!.Id);
        Assert.Equal(id, JsonSerializer.Deserialize<IdEnvelope>("{\"id\":7336899183492280321}", options)!.Id);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdEnvelope>("{\"id\":\"not-an-id\"}", options));
    }

    private sealed record IdEnvelope(long Id);
}
