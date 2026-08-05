using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using UsageTracker.Contracts;
using UsageTracker.Cost;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Normalization;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 5 / Increment 2 — the CloudEvents 1.0 usage-event API for coarse surfaces
/// (ARCHITECTURE.md §8.3 step 3). A UiPath AI-unit event parses → maps to a
/// credit-granularity span → prices via the CoarseUnit tier (no token math).
/// </summary>
public class CloudEventTests
{
    private readonly SpanMapperRegistry _reg =
        SpanMapperRegistry.CreateDefault(TokenNormalizerRegistry.CreateDefault());

    private const string UiPathEvent = """
    {
      "specversion": "1.0",
      "type": "com.uipath.ai.units",
      "source": "orchestrator/robot-7",
      "id": "evt-123",
      "time": "2026-08-05T10:00:00Z",
      "data": { "provider": "uipath", "granularity": "credit",
                "units_consumed": 2, "unit_type": "ai_unit" }
    }
    """;

    [Fact]
    public void CloudEvent_parses_and_maps_to_coarse_span()
    {
        var raw = CloudEventParser.Parse(UiPathEvent, "t", DateTimeOffset.UnixEpoch);
        Assert.Equal("cloudevent", raw.Dialect);
        Assert.Equal("uipath", raw.Attributes["provider"]);
        Assert.Equal(2L, raw.Attributes["units_consumed"]);

        var span = _reg.Map(raw);
        Assert.Equal(Granularity.Credit, span.Granularity);
        Assert.Equal(2L, span.UnitsConsumed);
        Assert.Equal("ai_unit", span.UnitType);
        Assert.Equal("uipath", span.Provider);
        Assert.Equal("usage-event", span.Metadata!["archetype"]);
        // time from the envelope wins over receivedAt
        Assert.Equal(DateTimeOffset.Parse("2026-08-05T10:00:00Z"), span.StartTime);
    }

    [Fact]
    public void CloudEvent_coarse_span_prices_via_coarse_unit_tier()
    {
        var engine = TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));
        var span = _reg.Map(CloudEventParser.Parse(UiPathEvent, "t", DateTimeOffset.UnixEpoch));
        var cost = engine.Cost(span)!;
        Assert.Equal("CoarseUnit", cost.Tier);
        Assert.Equal(0.40m, cost.TotalCost);   // 2 × $0.20 ai_unit
    }

    [Fact]
    public void Non_cloudevent_body_is_rejected()
    {
        Assert.Throws<JsonException>(() =>
            CloudEventParser.Parse("""{"just":"json"}""", "t", DateTimeOffset.UnixEpoch));
    }
}

/// <summary>
/// Phase 5 end-to-end: POST a CloudEvent to /v1/events, then confirm it flows
/// through map → (async) cost → store and rolls up with the coarse credit cost.
/// </summary>
public class CloudEventEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public CloudEventEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Usage_event_is_accepted_costed_and_queryable()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "ce-tenant");

        const string body = """
        { "specversion":"1.0", "type":"com.uipath.ai.units", "source":"orch/robot-1",
          "id":"ce-e2e-1",
          "data": { "provider":"uipath", "granularity":"credit", "units_consumed":2, "unit_type":"ai_unit" } }
        """;
        var resp = await client.PostAsync("/v1/events", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Async: poll the summary until the consumer drains.
        JsonElement summary = default;
        for (var i = 0; i < 50; i++)
        {
            summary = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
            if (summary.GetProperty("spanCount").GetInt32() >= 1) break;
            await Task.Delay(40);
        }
        Assert.True(summary.GetProperty("spanCount").GetInt32() >= 1, "consumer did not drain the CloudEvent");
        Assert.Equal(0.40m, summary.GetProperty("totalEstimatedCost").GetDecimal());   // 2 × $0.20
    }

    [Fact]
    public async Task Non_cloudevent_body_is_400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "ce-bad");
        var resp = await client.PostAsync("/v1/events",
            new StringContent("""{"not":"a cloudevent"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
