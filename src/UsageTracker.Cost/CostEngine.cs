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
    private readonly TimeProvider _clock;
    public int Order => 20;

    // Optional clock so CreateDefault(catalog) — used by tests and Program.cs — stays
    // unchanged; DI passes a real TimeProvider so CapturedAt is deterministic/testable.
    public PriceMapCostTier(IPriceCatalog catalog, TimeProvider? clock = null)
        => (_catalog, _clock) = (catalog, clock ?? TimeProvider.System);

    public CostBreakdown? TryCost(Span span)
    {
        // Token tier only — a coarse span (credits/seats/requests) is priced by
        // CoarseUnitCostTier. Defense in depth: even a hand-built coarse span that
        // erroneously carries Usage can never be mis-priced as tokens here.
        if (span.Granularity != Granularity.Token) return null;

        // Resolve once. PerHour rates (#8) are priced even when Usage is absent, so we
        // resolve before the usage check and branch on Mode below.
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

        if (rate.Mode == PricingMode.PerHour)
        {
            // Hours × HourlyRate; tokens ignored. Decimal from ticks avoids float drift.
            if (rate.HourlyRate is not { } hourly || span.EndTime is not { } end) return null;
            decimal hours = (decimal)(end - span.StartTime).Ticks / TimeSpan.TicksPerHour;
            if (hours <= 0) return null;
            decimal hourCost = hourly * hours;
            components.Add(new CostComponent("provisioned_hours", 0, hourly, hourCost));
            total = hourCost;
        }
        else
        {
            if (span.Usage is not { } u) return null;

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

            // Modality (#6): audio/image priced at their own per-token rate when the
            // rate declares one. Assumes these buckets are DISJOINT from the text
            // buckets (the normalizer owns that invariant) so there's no double count.
            Add("audio", u.AudioTokens, rate.AudioPerToken ?? 0m);
            Add("image", u.ImageTokens, rate.ImagePerToken ?? 0m);
        }

        // Geo/residency/service-tier multiplier (#9): applies to the token/hour
        // subtotal as an explicit adjustment line, so components still sum to total.
        if (rate.Multiplier != 1.0m && total > 0)
        {
            decimal adjusted = total * rate.Multiplier;
            components.Add(new CostComponent("geo_multiplier", 0, rate.Multiplier, adjusted - total));
            total = adjusted;
        }

        // Per-call tool surcharges (#7): stack ON TOP of the (multiplied) subtotal —
        // flat per-call fees are not subject to the geo multiplier.
        if (span.ToolCalls is { } tools)
        {
            foreach (var t in tools)
            {
                if (t.Count <= 0) continue;
                if (_catalog.ToolSurcharge(t.ToolType) is not { } perCall || perCall <= 0) continue;
                decimal sur = t.Count * perCall;
                components.Add(new CostComponent("tool:" + t.ToolType, t.Count, perCall, sur));
                total += sur;
            }
        }

        return new CostBreakdown
        {
            TotalCost = total,
            Currency = rate.Currency,
            Components = components,
            Tier = "PriceMap",
            CatalogVersion = rate.CatalogVersion,
            // Snapshot the WHOLE resolved rate + provenance so a later recompute
            // reproduces this exact figure even after the live catalog changes
            // (ARCHITECTURE.md §4.1; §5 #14). Any rate dimension added later is
            // captured automatically because we store the rate object itself.
            RateSnapshot = new RateSnapshot
            {
                Rate = rate,
                CatalogSourceId = rate.SourceId,
                CatalogVersion = rate.CatalogVersion,
                CapturedAt = _clock.GetUtcNow(),
                EffectiveFrom = rate.EffectiveFrom,
                TokenizerId = span.TokenizerId,
            },
        };
    }
}

/// <summary>
/// TIER 3 — last resort. When there is no ingested cost and no rate for the
/// model, we can't invent a price; return a zero-cost breakdown flagged
/// "Unpriced" so the event is still stored and visibly un-costed rather than
/// silently dropped. A tokenize-then-price tier (Order 40) sits above this and
/// handles unknown-count-but-known-text cases before we give up.
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
/// TIER 2.5 (Order 30) — coarse surfaces that don't speak tokens
/// (ARCHITECTURE.md §5 #15, §5 #8 per_unit/per_request/per_seat). Prices
/// <c>UnitsConsumed × PricePerUnit</c> for a non-Token granularity span
/// (UiPath "AI units", Copilot premium requests, seats). Sits below the token
/// price-map and above the unpriced fallback: a Token span is fully priced at
/// tier 20 and never reaches here; a coarse span leaves <c>Usage == null</c> so
/// tier 20 returns null and this tier catches it.
/// </summary>
public sealed class CoarseUnitCostTier : ICostTier
{
    private readonly IPriceCatalog _catalog;
    public int Order => 30;

    public CoarseUnitCostTier(IPriceCatalog catalog) => _catalog = catalog;

    public CostBreakdown? TryCost(Span span)
    {
        // Token spans belong to the token tiers — never touch them here.
        if (span.Granularity == Granularity.Token) return null;
        if (span.UnitsConsumed is not { } units || units <= 0) return null;
        if (span.UnitType is null) return null;

        var rate = _catalog.ResolveUnit(span);
        if (rate is null) return null;   // honest fall-through → Unpriced, not a fabricated price

        decimal cost = units * rate.PricePerUnit;    // long × decimal = decimal (money stays decimal)
        string kind = rate.Mode switch
        {
            PricingMode.PerRequest => "request",
            PricingMode.PerSeat => "seat",
            _ => "unit",
        };
        var component = new CostComponent(kind, units, rate.PricePerUnit, cost);
        return new CostBreakdown
        {
            TotalCost = cost,
            Currency = rate.Currency,
            Components = new[] { component },
            Tier = "CoarseUnit",
            CatalogVersion = rate.CatalogVersion,
        };
    }
}

/// <summary>
/// TIER 3 (Order 40) — tokenize-then-price (ARCHITECTURE.md §4.1 tier 3). Fires only
/// when a Token span has NO reported counts (<c>Usage == null</c>) but carries
/// transient <see cref="Span.EstimationText"/> and its model has a rate. Estimates
/// input tokens via the injected <see cref="ITokenizer"/>, records the tokenizer id
/// on the span (#10 drift attribution), and reuses the exact Tier-2 pricing so the
/// math is identical. Falls through to Unpriced when there's no text or no rate.
/// </summary>
public sealed class TokenizeThenPriceTier : ICostTier
{
    private readonly IPriceCatalog _catalog;
    private readonly ITokenizer _tokenizer;
    private readonly TimeProvider _clock;
    public int Order => 40;

    public TokenizeThenPriceTier(IPriceCatalog catalog, ITokenizer tokenizer, TimeProvider? clock = null)
        => (_catalog, _tokenizer, _clock) = (catalog, tokenizer, clock ?? TimeProvider.System);

    public CostBreakdown? TryCost(Span span)
    {
        if (span.Granularity != Granularity.Token) return null;
        if (span.Usage is not null) return null;                 // real counts → Tier-2 already handled it
        if (string.IsNullOrEmpty(span.EstimationText)) return null;
        if (_catalog.Resolve(span) is null) return null;         // no rate → let Unpriced own it

        long estimated = _tokenizer.CountTokens(span.EstimationText);
        var estimatedUsage = new NormalizedUsage
        {
            InputTokens = estimated,
            UncachedInputTokens = estimated,
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0,
            OutputTokens = 0,
            ReasoningOutputTokens = 0,
        };
        // Reuse Tier-2 pricing verbatim over the estimated usage + record the tokenizer id.
        var priced = new PriceMapCostTier(_catalog, _clock)
            .TryCost(span with { Usage = estimatedUsage, TokenizerId = _tokenizer.Id });
        if (priced is null) return null;

        // Relabel the tier so it's transparent this cost is an estimate, not a real count.
        return priced with { Tier = "Tokenized" };
    }
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
            new CoarseUnitCostTier(catalog),
            new UnpricedFallbackTier(),
        });

    /// <summary>Clock-injecting overload for DI so the rate snapshot's CapturedAt is
    /// deterministic and aligned with the app's <see cref="TimeProvider"/>.</summary>
    public static TieredCostEngine CreateDefault(IPriceCatalog catalog, TimeProvider clock) =>
        new(new ICostTier[]
        {
            new IngestedUsdCostTier(),
            new PriceMapCostTier(catalog, clock),
            new CoarseUnitCostTier(catalog),
            new UnpricedFallbackTier(),
        });

    /// <summary>Full chain incl. the Tier-3 tokenize-then-price fallback (Order 40),
    /// enabled by supplying an <see cref="ITokenizer"/>.</summary>
    public static TieredCostEngine CreateDefault(IPriceCatalog catalog, TimeProvider clock, ITokenizer tokenizer) =>
        new(new ICostTier[]
        {
            new IngestedUsdCostTier(),
            new PriceMapCostTier(catalog, clock),
            new CoarseUnitCostTier(catalog),
            new TokenizeThenPriceTier(catalog, tokenizer, clock),
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
