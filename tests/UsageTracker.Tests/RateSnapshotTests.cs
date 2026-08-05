using UsageTracker.Contracts;
using UsageTracker.Cost;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 3 / Increment 1 — rate snapshotting + historical recompute
/// (ARCHITECTURE.md §4.1 "store the rate, not just the cost"; §5 #14 date-effective,
/// #10 tokenizer drift). Prices are hand-computed from the seed catalog.
/// </summary>
public class RateSnapshotTests
{
    // Minimal deterministic clock so CapturedAt is assertable without a new package.
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-10T00:00:00Z");

    private static Span SpanWith(string model, NormalizedUsage usage) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Llm,
        Provider = "anthropic", ResponseModel = model, Usage = usage,
        StartTime = DateTimeOffset.UnixEpoch,
    };

    private static NormalizedUsage InOut(long input, long output) => new()
    {
        InputTokens = input, UncachedInputTokens = input,
        CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
        OutputTokens = output, ReasoningOutputTokens = 0,
    };

    // --- Case A: the snapshot is recorded with full provenance (#14) --------------
    [Fact]
    public void Tier2_records_rate_snapshot_with_provenance()
    {
        var engine = TieredCostEngine.CreateDefault(
            new PriceCatalog(OfflineBundleCatalogSource.Seed()), new FixedClock(At));

        var cost = engine.Cost(SpanWith("claude-opus-5", InOut(1000, 500)))!;

        Assert.Equal(0.0175m, cost.TotalCost);                 // 1000*5e-6 + 500*25e-6
        var snap = cost.RateSnapshot!;
        Assert.Equal(0.000025m, snap.Rate.OutputPerToken);     // whole resolved rate captured
        Assert.Equal(0.000005m, snap.Rate.InputPerToken);
        Assert.Equal("offline-bundle", snap.CatalogSourceId);
        Assert.Equal("seed-2026-08-04", snap.CatalogVersion);
        Assert.Equal(At, snap.CapturedAt);
    }

    // --- Case B: price change — snapshot reproduces, live re-prices (#14) ----------
    [Fact]
    public void Recompute_after_price_change_reproduces_original_from_snapshot()
    {
        var clock = new FixedClock(At);
        var seed = TieredCostEngine.CreateDefault(
            new PriceCatalog(OfflineBundleCatalogSource.Seed()), clock);

        var span = SpanWith("claude-opus-5", InOut(1000, 500));
        var original = seed.Cost(span)!;                       // 0.0175 @ seed-2026-08-04
        var stored = span with { EstimatedCost = original };

        // Live catalog now holds a HIGHER opus-5 output rate under a new version.
        const string bumped = """
        { "version": "seed-2026-09-01", "models": [
          { "model": "claude-opus-5", "input_per_token": 0.000005, "output_per_token": 0.000030,
            "cache_read_per_token": 0.0000005, "cache_creation_per_token": 0.00000625 } ] }
        """;
        var live = TieredCostEngine.CreateDefault(
            new PriceCatalog(new OfflineBundleCatalogSource(bumped)), clock);
        var rc = new SnapshotCostRecomputer(live);

        var fromSnap = rc.RecomputeFromSnapshot(stored)!;
        var fromLive = rc.RecomputeFromLiveCatalog(stored)!;

        Assert.Equal(0.0175m, fromSnap.TotalCost);             // 0.005 + 500*25e-6 — ORIGINAL reproduced
        Assert.Equal("seed-2026-08-04", fromSnap.RateSnapshot!.CatalogVersion);
        Assert.Equal(0.0200m, fromLive.TotalCost);             // 0.005 + 500*30e-6 — NEW price
        Assert.Equal("seed-2026-09-01", fromLive.RateSnapshot!.CatalogVersion);
    }

    // --- Case C: tokenizer drift raises cost at constant rate, attributable (#10) --
    [Fact]
    public void Tokenizer_drift_raises_cost_at_constant_rate_and_is_attributable()
    {
        var engine = TieredCostEngine.CreateDefault(
            new PriceCatalog(OfflineBundleCatalogSource.Seed()), new FixedClock(At));

        var v1 = engine.Cost(SpanWith("claude-opus-5", InOut(0, 1000)) with { TokenizerId = "claude-tokenizer.4-7" })!;
        var v2 = engine.Cost(SpanWith("claude-opus-5", InOut(0, 1300)) with { TokenizerId = "claude-tokenizer.4-8" })!;

        Assert.Equal(0.025m, v1.TotalCost);                    // 1000*25e-6
        Assert.Equal(0.0325m, v2.TotalCost);                   // 1300*25e-6 == 0.025 * 1.30
        // Rate is provably unchanged — the delta is tokenizer drift, not a price move:
        Assert.Equal(v1.RateSnapshot!.Rate.OutputPerToken, v2.RateSnapshot!.Rate.OutputPerToken);
        Assert.Equal("claude-tokenizer.4-7", v1.RateSnapshot!.TokenizerId);
        Assert.Equal("claude-tokenizer.4-8", v2.RateSnapshot!.TokenizerId);
    }

    // --- Ingested/Unpriced carry no replayable rate → recompute returns them as-is -
    [Fact]
    public void Recompute_from_snapshot_returns_ingested_and_unpriced_verbatim()
    {
        var live = TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));
        var rc = new SnapshotCostRecomputer(live);

        var ingested = SpanWith("claude-opus-5", InOut(1000, 500)) with
        {
            EstimatedCost = new CostBreakdown
            {
                TotalCost = 9.99m, Currency = "USD",
                Components = Array.Empty<CostComponent>(), Tier = "IngestedUsd",
            },
        };
        Assert.Equal(9.99m, rc.RecomputeFromSnapshot(ingested)!.TotalCost);

        var unpriced = SpanWith("no-such-model", InOut(1000, 500));
        var pricedUnpriced = live.Cost(unpriced)!;             // Tier "Unpriced", $0
        var stored = unpriced with { EstimatedCost = pricedUnpriced };
        Assert.Equal("Unpriced", rc.RecomputeFromSnapshot(stored)!.Tier);
    }

    // --- Recompute must reproduce TOOL SURCHARGES too (regression: caught live) ----
    // Surcharges come from the catalog, not the ModelRate, so a naive snapshot-replay
    // over just the rate drops them. They must be reconstructed from the components.
    [Fact]
    public void Recompute_from_snapshot_reproduces_tool_surcharges()
    {
        var live = TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));
        var rc = new SnapshotCostRecomputer(live);

        var span = SpanWith("claude-opus-5", InOut(1000, 500)) with
        {
            ToolCalls = new[] { new ToolCall("web_search", 2) },
        };
        var original = live.Cost(span)!;                       // 0.0175 tokens + 2*0.01 = 0.0375
        Assert.Equal(0.0375m, original.TotalCost);
        var stored = span with { EstimatedCost = original };

        var reproduced = rc.RecomputeFromSnapshot(stored)!;
        Assert.Equal(0.0375m, reproduced.TotalCost);           // surcharges reproduced, not dropped
    }

    // --- Legacy events (no RateSnapshot) still recompute via component rates -------
    [Fact]
    public void Recompute_from_legacy_breakdown_without_snapshot_reconstructs_rate()
    {
        var live = TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));
        var rc = new SnapshotCostRecomputer(live);

        // Simulate a pre-snapshot event: real components, but RateSnapshot stripped.
        var span = SpanWith("claude-opus-5", InOut(1000, 500));
        var costed = live.Cost(span)!;
        var legacy = costed with { RateSnapshot = null };
        var stored = span with { EstimatedCost = legacy };

        var reproduced = rc.RecomputeFromSnapshot(stored)!;
        Assert.Equal(0.0175m, reproduced.TotalCost);           // rebuilt from component RatePerUnit
    }
}
