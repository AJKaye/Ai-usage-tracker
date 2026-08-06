using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UsageTracker.Contracts;
using UsageTracker.Ingestion.Api;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 11 end-to-end (FinOps control plane): boots the real API in-process and drives
/// the full active-control path — create a budget → ingest spend → status reflects it →
/// exceed it → the background scan raises a budget_exceeded alert → forecast projects the
/// month → a seeded spike surfaces on /v1/anomalies. Tenant isolation asserted throughout.
/// </summary>
public class BudgetApiEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public BudgetApiEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private HttpClient ClientFor(string tenant)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return c;
    }

    // One anthropic event on claude-opus-5 (1000 in / 500 out → $0.0175, same as FinOps E2E).
    private static async Task IngestOne(HttpClient client, string spanId)
    {
        var body = $$"""
        { "gen_ai.provider.name":"anthropic", "gen_ai.response.model":"claude-opus-5",
          "gen_ai.usage.input_tokens":1000, "gen_ai.usage.output_tokens":500,
          "span_id":"{{spanId}}", "team_id":"alpha", "kind":"llm" }
        """;
        var r = await client.PostAsync("/v1/ingest", Json(body));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Budget_lifecycle_status_scan_alert_and_forecast()
    {
        const string tenant = "budget-e2e";
        var client = ClientFor(tenant);

        // Create a tiny monthly budget ($0.01) — one event ($0.0175) exceeds it.
        var created = await client.PostAsync("/v1/budgets",
            Json("""{ "limit": 0.01, "period": "monthly", "currency": "USD" }"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var budget = await created.Content.ReadFromJsonAsync<JsonElement>();
        var budgetId = budget.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(budgetId));

        // It shows up in the list.
        var list = await client.GetFromJsonAsync<JsonElement>("/v1/budgets");
        Assert.Equal(1, list.GetArrayLength());

        // Ingest spend that exceeds it.
        await IngestOne(client, "be-1");
        await IngestOne(client, "be-2");   // total 0.035 > 0.01

        // Status: evaluated live → exceeded, spent = 0.035.
        var status = await client.GetFromJsonAsync<JsonElement>("/v1/budgets/status");
        Assert.Equal(1, status.GetArrayLength());
        Assert.Equal("exceeded", status[0].GetProperty("state").GetString());
        Assert.Equal(0.035m, status[0].GetProperty("spentToDate").GetDecimal());

        // Forecast: month-to-date spend surfaced; projection ≥ spent-to-date (run-rate).
        var forecast = await client.GetFromJsonAsync<JsonElement>("/v1/forecast");
        Assert.Equal(0.035m, forecast.GetProperty("spentToDate").GetDecimal());
        Assert.True(forecast.GetProperty("projectedEndOfMonth").GetDecimal() >= 0.035m);

        // Run the background scan deterministically → it raises a budget_exceeded alert.
        var scan = _factory.Services.GetRequiredService<BudgetScanService>();
        await scan.ScanOnceAsync();

        var alerts = await client.GetFromJsonAsync<JsonElement>("/v1/alerts");
        Assert.True(alerts.GetArrayLength() >= 1);
        Assert.Contains(alerts.EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "budget_exceeded"
              && a.GetProperty("tenantId").GetString() == tenant);

        // De-dupe: a second scan does not pile on duplicate exceeded alerts for the same period.
        await scan.ScanOnceAsync();
        var alerts2 = await client.GetFromJsonAsync<JsonElement>("/v1/alerts");
        var exceededCount = alerts2.EnumerateArray()
            .Count(a => a.GetProperty("kind").GetString() == "budget_exceeded");
        Assert.Equal(1, exceededCount);

        // Delete removes it.
        var del = await client.DeleteAsync($"/v1/budgets/{budgetId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/v1/budgets");
        Assert.Equal(0, after.GetArrayLength());
    }

    [Fact]
    public async Task Anomalies_endpoint_flags_a_seeded_spike()
    {
        // Seed a flat baseline + a spike on the latest day directly into the store,
        // under a unique tenant so it can't collide with the budget test's data.
        const string tenant = "anomaly-e2e";
        var store = _factory.Services.GetRequiredService<IEventStore>();
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        for (int d = 7; d >= 1; d--)
            await store.AppendAsync(SpanOn(tenant, today.AddDays(-d), 10m, $"{tenant}-base-{d}"));
        await store.AppendAsync(SpanOn(tenant, today, 100m, $"{tenant}-spike"));   // clear outlier

        var client = ClientFor(tenant);
        var resp = await client.GetFromJsonAsync<JsonElement>("/v1/anomalies?baselineDays=7&k=3");
        var anomaly = resp.GetProperty("anomaly");
        Assert.Equal(JsonValueKind.Object, anomaly.ValueKind);   // not null → flagged
        Assert.Equal(100m, anomaly.GetProperty("cost").GetDecimal());
        Assert.Equal(10m, anomaly.GetProperty("baselineMean").GetDecimal());
    }

    [Fact]
    public async Task Tenants_do_not_see_each_others_budgets()
    {
        var a = ClientFor("iso-a");
        var b = ClientFor("iso-b");

        await a.PostAsync("/v1/budgets", Json("""{ "limit": 5, "period": "monthly" }"""));

        var seenByA = await a.GetFromJsonAsync<JsonElement>("/v1/budgets");
        var seenByB = await b.GetFromJsonAsync<JsonElement>("/v1/budgets");
        Assert.Equal(1, seenByA.GetArrayLength());
        Assert.Equal(0, seenByB.GetArrayLength());
    }

    [Fact]
    public async Task Budget_post_rejects_non_positive_limit()
    {
        var client = ClientFor("budget-bad");
        var resp = await client.PostAsync("/v1/budgets", Json("""{ "limit": 0, "period": "monthly" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static Span SpanOn(string tenant, DateOnly day, decimal cost, string spanId) => new()
    {
        TenantId = tenant, TraceId = spanId, SpanId = spanId, Kind = SpanKind.Llm,
        Provider = "anthropic", ResponseModel = "claude-opus-5",
        StartTime = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero),
        EstimatedCost = new CostBreakdown
        {
            TotalCost = cost, Currency = "USD",
            Components = Array.Empty<CostComponent>(), Tier = "PriceMap",
        },
    };
}
