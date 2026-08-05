using UsageTracker.Contracts;

namespace UsageTracker.Cost;

/// <summary>
/// A catalog that always serves ONE snapshotted rate, regardless of the current
/// live catalog. Backs snapshot replay: re-running the identical PriceMapCostTier
/// over the identical ModelRate reproduces the original cost bit-for-bit. Also
/// serves the tool surcharges the original event used — these come from the catalog
/// (not the ModelRate), so they must be replayed from the snapshot too, else
/// recompute silently drops them.
/// </summary>
public sealed class SnapshotReplayCatalog : IPriceCatalog
{
    private readonly ModelRate _rate;
    private readonly IReadOnlyDictionary<string, decimal> _toolSurcharges;

    public SnapshotReplayCatalog(ModelRate rate, IReadOnlyDictionary<string, decimal>? toolSurcharges = null)
        => (_rate, _toolSurcharges) = (rate, toolSurcharges ?? new Dictionary<string, decimal>());

    public ModelRate? Resolve(Span span) => _rate;
    public string Version => _rate.CatalogVersion;
    public decimal? ToolSurcharge(string toolType)
        => _toolSurcharges.TryGetValue(toolType, out var v) ? v : null;
}

/// <summary>
/// Recomputes a stored event's cost from its snapshot (stable) or against the
/// live catalog (re-baseline). See <see cref="ICostRecomputer"/>. Because the
/// snapshot path replays the SAME tier code over the SAME rate, it is guaranteed
/// to reproduce TotalCost and every CostComponent exactly — no parallel arithmetic
/// path to drift out of sync (ARCHITECTURE.md §4.1, §5 #14).
/// </summary>
public sealed class SnapshotCostRecomputer : ICostRecomputer
{
    private readonly ICostEngine _live;

    public SnapshotCostRecomputer(ICostEngine liveEngine) => _live = liveEngine;

    public CostBreakdown? RecomputeFromLiveCatalog(Span span) =>
        // Strip the prior estimate so Tier-1 (IngestedUsd) doesn't short-circuit and
        // return the old cost instead of re-pricing against today's catalog.
        _live.Cost(span with { EstimatedCost = null });

    public CostBreakdown? RecomputeFromSnapshot(Span span)
    {
        var original = span.EstimatedCost;
        if (original is null) return null;

        // IngestedUsd / Unpriced carry no rate-derived math — they ARE the truth.
        if (original.Tier is "IngestedUsd" or "Unpriced") return original;

        // Prefer the structured snapshot; fall back to reconstructing the rate from
        // per-component RatePerUnit for legacy events written before RateSnapshot existed.
        var rate = original.RateSnapshot?.Rate ?? ReconstructRate(span, original);
        if (rate is null) return original;

        // Tool surcharges (§5 #7) come from the catalog, not the ModelRate; reconstruct
        // the per-call rates the event used from its own "tool:*" components so the
        // replay reproduces them instead of dropping them.
        var surcharges = ReconstructToolSurcharges(original);

        var replay = TieredCostEngine.CreateDefault(new SnapshotReplayCatalog(rate, surcharges));
        var reproduced = replay.Cost(span with { EstimatedCost = null })!;

        // Preserve the ORIGINAL provenance (incl. CapturedAt) so the reproduced
        // breakdown is identical to what was stored, not stamped with recompute time.
        return reproduced with { RateSnapshot = original.RateSnapshot ?? reproduced.RateSnapshot };
    }

    // Rebuild the per-call tool surcharge rates from the original "tool:<type>"
    // components (each snapshots its RatePerUnit), keyed by tool type.
    private static IReadOnlyDictionary<string, decimal> ReconstructToolSurcharges(CostBreakdown b)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in b.Components)
            if (c.Kind.StartsWith("tool:", StringComparison.Ordinal))
                map[c.Kind["tool:".Length..]] = c.RatePerUnit;
        return map;
    }

    // Legacy path: rebuild a ModelRate from the snapshotted per-Kind component rates.
    private static ModelRate? ReconstructRate(Span span, CostBreakdown b)
    {
        var model = span.ResponseModel ?? span.RequestModel;
        if (model is null) return null;
        decimal RateFor(string k) => b.Components.FirstOrDefault(c => c.Kind == k)?.RatePerUnit ?? 0m;
        return new ModelRate
        {
            Model = model,
            Currency = b.Currency,
            InputPerToken = RateFor("input_uncached"),
            OutputPerToken = RateFor("output"),
            CacheReadPerToken = RateFor("cache_read"),
            CacheCreationPerToken = RateFor("cache_creation"),
            ReasoningPerToken = RateFor("reasoning"),
            CatalogVersion = b.CatalogVersion ?? "reconstructed",
        };
    }
}
