using System.Net;
using System.Text;
using UsageTracker.Contracts;
using UsageTracker.Reconciliation;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 4 / Increment 2 — billing connectors (ARCHITECTURE.md §4.3/§10). Parse
/// the OpenAI Costs + Anthropic cost_report response shapes into RealizedCost, over
/// an INJECTED HttpClient (canned responses; NO real network), with the API key
/// resolved from ISecretProvider (never config). The Anthropic Priority-Tier
/// exclusion (§5 #12) is surfaced.
/// </summary>
public class BillingConnectorTests
{
    private static readonly DateOnly Day = new(2026, 8, 5);

    // Canned HTTP handler — asserts the auth header was set, returns a fixed body.
    private sealed class CannedHandler(string body, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeSecrets(string? value) : ISecretProvider
    {
        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult(value);
    }

    private static HttpClient Client(HttpMessageHandler h) =>
        new(h) { BaseAddress = new Uri("https://api.invalid") };

    // --- OpenAI Costs API shape ---------------------------------------------------
    [Fact]
    public async Task OpenAi_connector_parses_costs_and_sends_bearer_key()
    {
        // Two line items in one daily bucket. start_time = 2026-08-05T00:00:00Z.
        long start = new DateTimeOffset(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        string body = $$"""
        { "data": [ {
            "start_time": {{start}},
            "results": [
              { "amount": { "value": 12.34, "currency": "usd" }, "line_item": "gpt-5.6" },
              { "amount": { "value": 0.66,  "currency": "usd" }, "line_item": "text-embedding-3" }
            ] } ] }
        """;
        string? sawAuth = null;
        var handler = new CannedHandler(body, req => sawAuth = req.Headers.Authorization?.ToString()
            ?? (req.Headers.TryGetValues("Authorization", out var v) ? string.Join("", v) : null));
        var connector = new OpenAiBillingConnector(Client(handler), new FakeSecrets("sk-admin-123"));

        var rows = await connector.PullAsync("t", Day, Day);

        Assert.Equal(2, rows.Count);
        Assert.Equal(12.34m, rows[0].Amount);
        Assert.Equal("gpt-5.6", rows[0].Model);
        Assert.Equal(Day, rows[0].Day);
        Assert.Equal("USD", rows[0].Currency);
        Assert.All(rows, r => Assert.Equal("openai", r.Provider));
        Assert.Contains("Bearer sk-admin-123", sawAuth);   // key from ISecretProvider, on the wire
    }

    [Fact]
    public async Task OpenAi_connector_throws_when_secret_missing()
    {
        var connector = new OpenAiBillingConnector(Client(new CannedHandler("{}")), new FakeSecrets(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.PullAsync("t", Day, Day));
    }

    // --- Anthropic cost_report shape ----------------------------------------------
    [Fact]
    public async Task Anthropic_connector_parses_cost_report_and_sends_api_key()
    {
        string body = """
        { "data": [ {
            "starting_at": "2026-08-05T00:00:00Z",
            "results": [
              { "amount": "9.99", "currency": "USD", "model": "claude-opus-5" },
              { "amount": 1.01,   "currency": "USD", "model": "claude-sonnet-5" }
            ] } ] }
        """;
        string? sawKey = null; string? sawVersion = null;
        var handler = new CannedHandler(body, req =>
        {
            sawKey = req.Headers.TryGetValues("x-api-key", out var k) ? string.Join("", k) : null;
            sawVersion = req.Headers.TryGetValues("anthropic-version", out var v) ? string.Join("", v) : null;
        });
        var connector = new AnthropicBillingConnector(Client(handler), new FakeSecrets("sk-ant-admin"));

        var rows = await connector.PullAsync("t", Day, Day);

        Assert.Equal(2, rows.Count);
        Assert.Equal(9.99m, rows[0].Amount);              // string amount parsed
        Assert.Equal(1.01m, rows[1].Amount);              // numeric amount parsed
        Assert.Equal("claude-opus-5", rows[0].Model);
        Assert.All(rows, r => Assert.Equal("anthropic", r.Provider));
        Assert.Equal("sk-ant-admin", sawKey);
        Assert.Equal("2023-06-01", sawVersion);
        Assert.True(connector.ExcludesPriorityTier);       // §5 #12 caveat surfaced
        Assert.All(rows, r => Assert.Contains("priority", r.CostType!));
    }

    // --- connector plugs straight into the reconciler -----------------------------
    [Fact]
    public async Task Connectors_feed_the_reconciler_end_to_end()
    {
        var store = new UsageTracker.Storage.InMemory.InMemoryEventStore();
        await store.AppendAsync(new Span
        {
            TenantId = "t", TraceId = "tr", SpanId = "s1", Kind = SpanKind.Llm, Provider = "openai",
            StartTime = new DateTimeOffset(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            EstimatedCost = new CostBreakdown
            {
                TotalCost = 12.00m, Currency = "USD",
                Components = Array.Empty<CostComponent>(), Tier = "PriceMap",
            },
        });

        string body = $$"""
        { "data": [ { "start_time": {{new DateTimeOffset(Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds()}},
            "results": [ { "amount": { "value": 12.34, "currency": "usd" }, "line_item": "gpt-5.6" } ] } ] }
        """;
        var connector = new OpenAiBillingConnector(Client(new CannedHandler(body)), new FakeSecrets("sk"));
        var reconciler = new CostReconciler(store, new IBillingConnector[] { connector });

        var r = await reconciler.ReconcileAsync("t", Day);
        Assert.Equal(12.00m, r.EstimatedTotal);
        Assert.Equal(12.34m, r.RealizedTotal);
        Assert.Equal(0.34m, r.Delta);                      // estimate was 0.34 low vs realized
    }
}
