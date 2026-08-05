using UsageTracker.Contracts;
using UsageTracker.Reconciliation;
using UsageTracker.Storage.InMemory;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 4 / Increment 1 — the reconciler core (ARCHITECTURE.md §4.1 / §8.3 step 2):
/// estimated (from stored spans) vs realized (from billing connectors), delta per
/// provider, graceful degradation, and air-gap (no-connector) behavior.
/// </summary>
public class ReconcilerTests
{
    private static readonly DateOnly Day = new(2026, 8, 5);

    // A fake connector returning canned realized rows — or throwing, to test degradation.
    private sealed class FakeConnector(string provider, IReadOnlyList<RealizedCost>? rows, bool throws = false) : IBillingConnector
    {
        public string Provider => provider;
        public Task<IReadOnlyList<RealizedCost>> PullAsync(string tenantId, DateOnly from, DateOnly to, CancellationToken ct = default)
            => throws
                ? throw new HttpRequestException("simulated billing API outage")
                : Task.FromResult(rows ?? Array.Empty<RealizedCost>());
    }

    private static Span CostedSpan(string spanId, string provider, decimal cost, DateOnly day) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = spanId, Kind = SpanKind.Llm,
        Provider = provider,
        StartTime = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        EstimatedCost = new CostBreakdown
        {
            TotalCost = cost, Currency = "USD",
            Components = Array.Empty<CostComponent>(), Tier = "PriceMap",
        },
    };

    private static async Task<IEventStore> StoreWith(params Span[] spans)
    {
        var store = new InMemoryEventStore();
        foreach (var s in spans) await store.AppendAsync(s);
        return store;
    }

    private static RealizedCost Realized(string provider, decimal amount, DateOnly day) => new()
    {
        Provider = provider, Day = day, Amount = amount, Currency = "USD",
    };

    // --- estimated vs realized delta, per provider --------------------------------
    [Fact]
    public async Task Reconciles_estimated_against_realized_with_per_provider_delta()
    {
        var store = await StoreWith(
            CostedSpan("s1", "anthropic", 0.0175m, Day),
            CostedSpan("s2", "anthropic", 0.0080m, Day),
            CostedSpan("s3", "openai", 0.0050m, Day));

        // Realized (authoritative) billing: anthropic a touch higher, openai a touch lower.
        var connectors = new IBillingConnector[]
        {
            new FakeConnector("anthropic", new[] { Realized("anthropic", 0.0300m, Day) }),
            new FakeConnector("openai", new[] { Realized("openai", 0.0040m, Day) }),
        };
        var results = new InMemoryReconciliationStore();
        var reconciler = new CostReconciler(store, connectors, results);

        var r = await reconciler.ReconcileAsync("t", Day);

        Assert.True(r.ReconciledAgainstBilling);
        Assert.Equal(0.0305m, r.EstimatedTotal);        // 0.0175 + 0.0080 + 0.0050
        Assert.Equal(0.0340m, r.RealizedTotal);         // 0.0300 + 0.0040
        Assert.Equal(0.0035m, r.Delta);                 // 0.0340 - 0.0305

        var anthropic = Assert.Single(r.ByProvider, p => p.Provider == "anthropic");
        Assert.Equal(0.0255m, anthropic.Estimated);     // 0.0175 + 0.0080
        Assert.Equal(0.0300m, anthropic.Realized);
        Assert.Equal(0.0045m, anthropic.Delta);

        var openai = Assert.Single(r.ByProvider, p => p.Provider == "openai");
        Assert.Equal(-0.0010m, openai.Delta);           // realized 0.0040 - estimated 0.0050

        // Persisted at the tenant-day grain.
        var saved = await results.GetAsync("t", Day);
        Assert.Equal(0.0035m, saved!.Delta);
    }

    // --- air-gap / no connector: estimate stands, not reconciled ------------------
    [Fact]
    public async Task No_connector_leaves_estimate_standing_and_flags_not_reconciled()
    {
        var store = await StoreWith(CostedSpan("s1", "anthropic", 0.0175m, Day));
        var reconciler = new CostReconciler(store, Array.Empty<IBillingConnector>());

        var r = await reconciler.ReconcileAsync("t", Day);

        Assert.False(r.ReconciledAgainstBilling);       // surfaced, not silently zero
        Assert.Equal(0.0175m, r.EstimatedTotal);
        Assert.Equal(0m, r.RealizedTotal);
    }

    // --- a connector failure degrades gracefully: estimate remains ----------------
    [Fact]
    public async Task Connector_failure_degrades_gracefully_estimate_remains()
    {
        var store = await StoreWith(
            CostedSpan("s1", "anthropic", 0.0175m, Day),
            CostedSpan("s2", "openai", 0.0050m, Day));

        var connectors = new IBillingConnector[]
        {
            new FakeConnector("anthropic", null, throws: true),                    // outage
            new FakeConnector("openai", new[] { Realized("openai", 0.0050m, Day) }),
        };
        var reconciler = new CostReconciler(store, connectors);

        var r = await reconciler.ReconcileAsync("t", Day);

        // Run still succeeds; anthropic estimate present with realized 0 (unreconciled),
        // openai fully reconciled with a zero delta.
        Assert.Equal(0.0225m, r.EstimatedTotal);
        Assert.Equal(0.0050m, r.RealizedTotal);         // only openai came back
        var anthropic = Assert.Single(r.ByProvider, p => p.Provider == "anthropic");
        Assert.Equal(0.0175m, anthropic.Estimated);
        Assert.Equal(0m, anthropic.Realized);
    }

    // --- day scoping: only the requested day's spans count ------------------------
    [Fact]
    public async Task Only_the_requested_day_is_reconciled()
    {
        var store = await StoreWith(
            CostedSpan("today", "anthropic", 0.0175m, Day),
            CostedSpan("yesterday", "anthropic", 9.9999m, Day.AddDays(-1)));
        var reconciler = new CostReconciler(store, Array.Empty<IBillingConnector>());

        var r = await reconciler.ReconcileAsync("t", Day);
        Assert.Equal(0.0175m, r.EstimatedTotal);        // yesterday's big span excluded
    }
}
