using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 10 / Increment 2 — platform self-observability. /v1/platform/stats reports
/// uptime + ingest throughput + queue depth + backend tier (dogfood). Public (an ops
/// signal, no tenant data).
/// </summary>
public class PlatformStatsTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public PlatformStatsTests(EphemeralApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Stats_endpoint_is_public_and_reports_shape()
    {
        var client = _factory.CreateClient();   // no auth header — it's a public ops signal
        var resp = await client.GetAsync("/v1/platform/stats");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var s = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ai-usage-tracker", s.GetProperty("service").GetString());
        Assert.Equal("ephemeral", s.GetProperty("profile").GetString());
        Assert.True(s.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.True(s.TryGetProperty("ingest", out var ingest));
        Assert.True(ingest.TryGetProperty("enqueued", out _));
        Assert.True(ingest.TryGetProperty("processed", out _));
        Assert.True(ingest.TryGetProperty("queueDepth", out _));
    }

    [Fact]
    public async Task Ingest_counters_advance_after_an_event_is_processed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "stats-tenant");

        // Ingest via the async /v1/traces path so it flows through the channel + consumer.
        var otlp = """
        { "resourceSpans":[{ "scopeSpans":[{ "spans":[{
          "traceId":"t","spanId":"stats-1","name":"chat","attributes":[
            {"key":"gen_ai.provider.name","value":{"stringValue":"anthropic"}},
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"100"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"50"}} ] }] }] }] }
        """;
        var acc = await client.PostAsync("/v1/traces", new StringContent(otlp, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, acc.StatusCode);

        // Poll stats until the consumer has processed at least one span.
        long processed = 0;
        for (var i = 0; i < 50; i++)
        {
            var s = await client.GetFromJsonAsync<JsonElement>("/v1/platform/stats");
            processed = s.GetProperty("ingest").GetProperty("processed").GetInt64();
            if (processed >= 1) break;
            await Task.Delay(40);
        }
        Assert.True(processed >= 1, "ingest 'processed' counter did not advance");
    }
}
