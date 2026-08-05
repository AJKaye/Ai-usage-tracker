using UsageTracker.Contracts;

namespace UsageTracker.Cost;

/// <summary>
/// TIER 1 — use directly-ingested USD if the source already provided a cost
/// (a gateway or provider that returns cost). Most accurate; run first.
/// </summary>
public sealed class IngestedUsdCostTier : ICostTier
{
    public int Order => 10;

    public CostBreakdown? TryCost(Span span)
    {
        // If an upstream already attached a cost with the "IngestedUsd" tier, honor it.
        var c = span.EstimatedCost;
        if (c is not null && string.Equals(c.Tier, "IngestedUsd", StringComparison.Ordinal))
            return c;
        return null;
    }
}

/// <summary>
/// TIER 2 — token counts × per-model price map. The workhorse. Prices each token
/// bucket at its own rate (base / cache-read / cache-creation / reasoning), so
/// caching and reasoning are costed correctly rather than at a flat input rate
/// (ARCHITECTURE.md §5 gotchas #2, #3). Snapshots each rate into the component.
/// </summary>
public sealed class PriceMapCostTier : ICostTier
{
    private readonly IPriceCatalog _catalog;
    public int Order => 20;

    public PriceMapCostTier(IPriceCatalog catalog) => _catalog = catalog;

    public CostBreakdown? TryCost(Span span)
    {
        if (span.Usage is not { } u) return null;
        var rate = _catalog.Resolve(span);
        if (rate is null) return null;

        var components = new List<CostComponent>();
        decimal total = 0m;

        void Add(string kind, long units, decimal perUnit)
        {
            if (units <= 0 || perUnit <= 0) return;
            decimal cost = units * perUnit;
            components.Add(new CostComponent(kind, units, perUnit, cost));
            total += cost;
        }

        // Input is split into three separately-priced buckets. Note: reasoning is
        // part of OutputTokens already, so we price (output − reasoning) at the
        // output rate and reasoning at its own rate (defaults to output rate).
        decimal reasoningRate = rate.ReasoningPerToken ?? rate.OutputPerToken;
        long nonReasoningOutput = Math.Max(0, u.OutputTokens - u.ReasoningOutputTokens);

        Add("input_uncached", u.UncachedInputTokens, rate.InputPerToken);
        Add("cache_read", u.CacheReadInputTokens, rate.CacheReadPerToken);
        Add("cache_creation", u.CacheCreationInputTokens, rate.CacheCreationPerToken);
        Add("output", nonReasoningOutput, rate.OutputPerToken);
        Add("reasoning", u.ReasoningOutputTokens, reasoningRate);

        return new CostBreakdown
        {
            TotalCost = total,
            Currency = rate.Currency,
            Components = components,
            Tier = "PriceMap",
            CatalogVersion = rate.CatalogVersion,
        };
    }
}

/// <summary>
/// TIER 3 — last resort. When there is no ingested cost and no rate for the
/// model, we can't invent a price; return a zero-cost breakdown flagged
/// "Unpriced" so the event is still stored and visibly un-costed rather than
/// silently dropped. (A real tokenize-then-price tier for unknown models plugs
/// in here later.)
/// </summary>
public sealed class UnpricedFallbackTier : ICostTier
{
    public int Order => 90;

    public CostBreakdown TryCostAlways(Span span) => new()
    {
        TotalCost = 0m,
        Currency = "USD",
        Components = Array.Empty<CostComponent>(),
        Tier = "Unpriced",
        CatalogVersion = null,
    };

    public CostBreakdown? TryCost(Span span) => TryCostAlways(span);
}

/// <summary>
/// Runs the tier chain in order; first non-null wins (ARCHITECTURE.md §4.1).
/// Tiers are injected, so the chain is fully reconfigurable.
/// </summary>
public sealed class TieredCostEngine : ICostEngine
{
    private readonly IReadOnlyList<ICostTier> _tiers;

    public TieredCostEngine(IEnumerable<ICostTier> tiers) =>
        _tiers = tiers.OrderBy(t => t.Order).ToList();

    public static TieredCostEngine CreateDefault(IPriceCatalog catalog) =>
        new(new ICostTier[]
        {
            new IngestedUsdCostTier(),
            new PriceMapCostTier(catalog),
            new UnpricedFallbackTier(),
        });

    public CostBreakdown? Cost(Span span)
    {
        foreach (var tier in _tiers)
        {
            var result = tier.TryCost(span);
            if (result is not null) return result;
        }
        return null;
    }
}
