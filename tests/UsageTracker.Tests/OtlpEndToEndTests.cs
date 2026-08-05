using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 2 end-to-end: POST a REAL OTLP/HTTP JSON trace-export body to /v1/traces,
/// then confirm the spans flow through map → (async) cost → store and become
/// queryable with correct multi-bucket tokens and cost. Because ingestion is now
/// async (channel + background consumer), the test polls /v1/summary until the
/// consumer has drained — the honest way to assert an async pipeline.
/// Uses the ephemeral (in-memory) profile via EphemeralApiFactory.
/// </summary>
public class OtlpEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public OtlpEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    private const string OtlpBody = """
    {
      "resourceSpans": [{
        "scopeSpans": [{
          "spans": [{
            "traceId": "otlpe2e", "spanId": "otlp-span-1", "name": "chat",
            "attributes": [
              { "key": "gen_ai.provider.name", "value": { "stringValue": "anthropic" } },
              { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
              { "key": "gen_ai.response.model", "value": { "stringValue": "claude-opus-5" } },
              { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "200" } },
              { "key": "gen_ai.usage.cache_read.input_tokens", "value": { "intValue": "600" } },
              { "key": "gen_ai.usage.cache_creation.input_tokens", "value": { "intValue": "200" } },
              { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "500" } }
            ]
          }]
        }]
      }]
    }
    """;

    [Fact]
    public async Task Otlp_trace_is_accepted_mapped_costed_and_queryable()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "otlp-tenant");

        var resp = await client.PostAsync("/v1/traces",
            new StringContent(OtlpBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using (var accDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            Assert.Equal(1, accDoc.RootElement.GetProperty("accepted").GetInt32());

        // Async: poll until the background consumer has persisted + costed the span.
        JsonElement summary = default;
        for (var i = 0; i < 50; i++)
        {
            summary = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
            if (summary.GetProperty("spanCount").GetInt32() >= 1) break;
            await Task.Delay(40);
        }

        Assert.True(summary.GetProperty("spanCount").GetInt32() >= 1, "consumer did not drain the OTLP span in time");
        // Anthropic additive: true input = 200 + 600 + 200 = 1000
        Assert.Equal(1000, summary.GetProperty("totalInputTokens").GetInt64());
        Assert.True(summary.GetProperty("totalEstimatedCost").GetDecimal() > 0m);

        var span = await client.GetFromJsonAsync<JsonElement>("/v1/spans/otlp-span-1");
        Assert.Equal("claude-opus-5", span.GetProperty("responseModel").GetString());
    }

    [Fact]
    public async Task Oversized_trace_is_rejected_413_without_ingest()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "otlp-big");
        // > 32 MB body → must be rejected outright, no partial ingest.
        var huge = new string('x', 33 * 1024 * 1024);
        var resp = await client.PostAsync("/v1/traces",
            new StringContent(huge, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);

        var summary = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
        Assert.Equal(0, summary.GetProperty("spanCount").GetInt32());
    }

    [Fact]
    public async Task Malformed_json_is_rejected_400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "otlp-bad");
        var resp = await client.PostAsync("/v1/traces",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
