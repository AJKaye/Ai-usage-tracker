using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 4 end-to-end: ingest costed events, then POST /v1/reconcile for the day
/// and read the result back with GET. The ephemeral profile registers NO billing
/// connectors, so this exercises the air-gap path: the estimate stands and
/// reconciledAgainstBilling is false (surfaced, not silently zeroed).
/// </summary>
public class ReconcileEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public ReconcileEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Reconcile_endpoint_returns_estimate_standing_in_airgap_mode()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "recon-tenant");

        // Ingest one costed anthropic event (additive: 1000 input, 500 output).
        var body = """
        {
          "gen_ai.provider.name": "anthropic",
          "gen_ai.response.model": "claude-opus-5",
          "gen_ai.usage.input_tokens": 1000,
          "gen_ai.usage.output_tokens": 500,
          "span_id": "recon-s1",
          "start_time": "2026-08-05T12:00:00Z"
        }
        """;
        var ingest = await client.PostAsync("/v1/ingest",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        // Reconcile that day.
        var post = await client.PostAsync("/v1/reconcile?day=2026-08-05", content: null);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Estimate present; no connectors → not reconciled against billing.
        Assert.Equal(0.0175m, root.GetProperty("estimatedTotal").GetDecimal());  // 1000*5e-6 + 500*25e-6
        Assert.Equal(0m, root.GetProperty("realizedTotal").GetDecimal());
        Assert.False(root.GetProperty("reconciledAgainstBilling").GetBoolean());

        // GET returns the stored result.
        var get = await client.GetFromJsonAsync<JsonElement>("/v1/reconcile?day=2026-08-05");
        Assert.Equal(0.0175m, get.GetProperty("estimatedTotal").GetDecimal());
    }

    [Fact]
    public async Task Reconcile_get_unknown_day_is_404()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "recon-empty");
        var resp = await client.GetAsync("/v1/reconcile?day=2000-01-01");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
