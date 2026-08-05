using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 6 end-to-end: ingest events, then exercise the FinOps serving API —
/// allocation (tag-free), unit economics, FOCUS rows, efficiency, and score
/// aggregation — over the ephemeral profile.
/// </summary>
public class FinOpsApiEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public FinOpsApiEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [Fact]
    public async Task FinOps_serving_endpoints_work_end_to_end()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "finops-e2e");

        // Two anthropic events on claude-opus-5 (additive; each 1000 in / 500 out → $0.0175).
        for (int i = 0; i < 2; i++)
        {
            var body = $$"""
            { "gen_ai.provider.name":"anthropic", "gen_ai.response.model":"claude-opus-5",
              "gen_ai.usage.input_tokens":1000, "gen_ai.usage.output_tokens":500,
              "span_id":"fo-{{i}}", "team_id":"alpha", "kind":"llm" }
            """;
            var r = await client.PostAsync("/v1/ingest", Json(body));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        // Allocation by provider → one bucket "anthropic" holding the full spend.
        var alloc = await client.GetFromJsonAsync<JsonElement>("/v1/allocation?dimension=provider");
        Assert.Equal("provider", alloc.GetProperty("dimension").GetString());
        Assert.Equal(0.035m, alloc.GetProperty("total").GetDecimal());        // 2 × 0.0175
        var buckets = alloc.GetProperty("buckets");
        Assert.Equal("anthropic", buckets[0].GetProperty("key").GetString());
        Assert.Equal(0.035m, buckets[0].GetProperty("cost").GetDecimal());

        // Allocation by team → "alpha".
        var byTeam = await client.GetFromJsonAsync<JsonElement>("/v1/allocation?dimension=team");
        Assert.Equal("alpha", byTeam.GetProperty("buckets")[0].GetProperty("key").GetString());

        // Unit economics: 0.035 / 3000 tokens; 0.035 / 2 inferences; 0.035 / 7 outcomes.
        var ue = await client.GetFromJsonAsync<JsonElement>("/v1/unit-economics?outcomes=7");
        Assert.Equal(0.0175m, ue.GetProperty("costPerInference").GetDecimal());   // 0.035 / 2
        Assert.Equal(0.005m, ue.GetProperty("costPerOutcome").GetDecimal());      // 0.035 / 7

        // FOCUS rows: two Usage rows, tokens consumption.
        var focus = await client.GetFromJsonAsync<JsonElement>("/v1/focus");
        Assert.Equal(2, focus.GetArrayLength());
        Assert.Equal("tokens", focus[0].GetProperty("consumedUnit").GetString());
        Assert.Equal(1500m, focus[0].GetProperty("consumedQuantity").GetDecimal());

        // Efficiency: 2 spans.
        var eff = await client.GetFromJsonAsync<JsonElement>("/v1/efficiency");
        Assert.Equal(2, eff.GetProperty("spanCount").GetInt32());

        // Score aggregation: attach then read back.
        var post = await client.PostAsync("/v1/scores",
            Json("""{ "target_id":"fo-0", "name":"helpfulness", "numeric":0.91, "source":"ragas" }"""));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var scores = await client.GetFromJsonAsync<JsonElement>("/v1/spans/fo-0/scores");
        Assert.Equal(1, scores.GetArrayLength());
        Assert.Equal("helpfulness", scores[0].GetProperty("name").GetString());
        Assert.Equal(0.91, scores[0].GetProperty("numeric").GetDouble(), 3);
    }

    [Fact]
    public async Task Score_post_requires_target_and_name()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "finops-bad");
        var resp = await client.PostAsync("/v1/scores", Json("""{ "numeric": 1.0 }"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
