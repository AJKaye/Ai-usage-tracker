using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Boots the API forcing the "ephemeral" profile so the end-to-end tests use the
/// volatile in-memory store — hermetic, no stray SQLite file, no cross-run bleed.
/// (The real .exe defaults to the "solo" SQLite profile; that path is covered by
/// the published-.exe smoke run, not these in-process tests.)
/// </summary>
public sealed class EphemeralApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        => builder.UseSetting("Profile", "ephemeral");
}

/// <summary>
/// END-TO-END slice test: boots the real ingestion API in-process
/// (WebApplicationFactory) and drives the full path — POST a gen_ai.* event →
/// normalize → cost → store → GET it back → summary rolls up. Proves the vertical
/// slice actually runs, not just that units pass.
/// </summary>
public class IngestionEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public IngestionEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Ingest_anthropic_event_normalizes_costs_and_persists()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "acme");

        // Anthropic (additive family): input_tokens is the uncached remainder.
        var payload = new Dictionary<string, object?>
        {
            ["gen_ai.provider.name"] = "anthropic",
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "claude-opus-5",
            ["gen_ai.usage.input_tokens"] = 200,
            ["gen_ai.usage.cache_read.input_tokens"] = 600,
            ["gen_ai.usage.cache_creation.input_tokens"] = 200,
            ["gen_ai.usage.output_tokens"] = 500,
            ["span_id"] = "sp-e2e-1",
            ["trace_id"] = "tr-e2e-1",
        };

        var post = await client.PostAsJsonAsync("/v1/ingest", payload);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // normalization: true input = 200 + 600 + 200 = 1000 (cache added back)
        Assert.Equal(1000, root.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        // cost tier is the price map, with a real total
        var cost = root.GetProperty("cost");
        Assert.Equal("PriceMap", cost.GetProperty("tier").GetString());
        Assert.True(cost.GetProperty("totalCost").GetDecimal() > 0m);

        // round-trip: GET the span back
        var get = await client.GetAsync("/v1/spans/sp-e2e-1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        // summary rolls up the tenant's spend
        var summary = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
        Assert.True(summary.GetProperty("spanCount").GetInt32() >= 1);
        Assert.True(summary.GetProperty("totalEstimatedCost").GetDecimal() > 0m);
    }

    [Fact]
    public async Task Tenants_are_isolated()
    {
        var acme = _factory.CreateClient();
        acme.DefaultRequestHeaders.Add("X-Tenant-Id", "acme-iso");
        await acme.PostAsJsonAsync("/v1/ingest", new Dictionary<string, object?>
        {
            ["gen_ai.provider.name"] = "openai",
            ["gen_ai.response.model"] = "gpt-5.6",
            ["gen_ai.usage.input_tokens"] = 100,
            ["gen_ai.usage.output_tokens"] = 50,
            ["span_id"] = "sp-iso-1",
        });

        // A different tenant must not see acme's span.
        var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Tenant-Id", "other-iso");
        var get = await other.GetAsync("/v1/spans/sp-iso-1");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_responds()
    {
        var res = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
