using UsageTracker.Contracts;
using UsageTracker.Cost;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 3 / Increment 2 — per-granularity pricing for coarse surfaces
/// (ARCHITECTURE.md §5 #15 "coarse surfaces don't speak tokens"; §5 #8
/// per_unit/per_request/per_seat). Prices are hand-computed from the seed
/// catalog's unit_rates block. Confirms the token path is untouched.
/// </summary>
public class CoarseUnitCostTests
{
    private static ICostEngine Engine() =>
        TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));

    private static Span Coarse(Granularity g, long units, string unitType, string provider, string? model = null) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Tool,
        Provider = provider, ResponseModel = model,
        Granularity = g, UnitsConsumed = units, UnitType = unitType,
        StartTime = DateTimeOffset.UnixEpoch,
    };

    private static Span TokenSpan(string model, NormalizedUsage usage) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Llm,
        Provider = "anthropic", ResponseModel = model, Usage = usage,
        StartTime = DateTimeOffset.UnixEpoch,
    };

    [Fact] // (i) UiPath GenAI activity w/ Context Grounding: 2 AI units @ $0.20
    public void UiPath_credits_priced_units_times_rate()
    {
        var cost = Engine().Cost(Coarse(Granularity.Credit, 2, "ai_unit", "uipath"))!;
        Assert.Equal("CoarseUnit", cost.Tier);
        Assert.Equal(0.40m, cost.TotalCost);                 // 2 × 0.20
        var comp = Assert.Single(cost.Components);
        Assert.Equal("unit", comp.Kind);
        Assert.Equal(2, comp.Units);
        Assert.Equal(0.20m, comp.RatePerUnit);               // rate snapshotted into the component
        Assert.Equal("seed-2026-08-04", cost.CatalogVersion);
    }

    [Fact] // (ii) Copilot premium-request overage: 5 requests @ $0.04 = $0.20
    public void Copilot_premium_requests_priced_per_request()
    {
        var cost = Engine().Cost(Coarse(Granularity.Request, 5, "premium_request", "github"))!;
        Assert.Equal("CoarseUnit", cost.Tier);
        Assert.Equal(0.20m, cost.TotalCost);
        Assert.Equal("request", cost.Components[0].Kind);
    }

    [Fact] // (iii) Seats: 3 seats @ $19.00 = $57.00
    public void Copilot_seats_priced_per_seat()
    {
        var cost = Engine().Cost(Coarse(Granularity.Seat, 3, "copilot_seat", "github"))!;
        Assert.Equal("CoarseUnit", cost.Tier);
        Assert.Equal(57.00m, cost.TotalCost);
        Assert.Equal("seat", cost.Components[0].Kind);
    }

    [Fact] // (iv) CONTROL: a token span is still priced by the token tier; coarse tier inert
    public void Token_span_unaffected_by_coarse_tier()
    {
        var usage = new NormalizedUsage
        {
            InputTokens = 1000, UncachedInputTokens = 1000, CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0, OutputTokens = 500, ReasoningOutputTokens = 0,
        };
        var span = TokenSpan("claude-opus-5", usage);        // Granularity defaults to Token
        var cost = Engine().Cost(span)!;
        Assert.Equal("PriceMap", cost.Tier);                 // NOT CoarseUnit
        Assert.Equal(0.0175m, cost.TotalCost);

        // The coarse tier itself refuses a Token span outright.
        var tier = new CoarseUnitCostTier(new PriceCatalog(OfflineBundleCatalogSource.Seed()));
        Assert.Null(tier.TryCost(span));
    }

    [Fact] // (v) composite key: model-specific premium-request rate wins over generic
    public void Premium_request_model_multiplier_uses_most_specific_rate()
    {
        var cost = Engine().Cost(Coarse(Granularity.Request, 5, "premium_request", "github", "gpt-5.6-max"))!;
        Assert.Equal(1.00m, cost.TotalCost);                 // 5 × 0.20, not 5 × 0.04
        Assert.Equal(0.20m, cost.Components[0].RatePerUnit);
    }

    [Fact] // (vi) unknown unit type → honest zero, never a fabricated price
    public void Unknown_unit_type_falls_through_to_unpriced()
    {
        var cost = Engine().Cost(Coarse(Granularity.Credit, 10, "mystery_credit", "acme"))!;
        Assert.Equal("Unpriced", cost.Tier);
        Assert.Equal(0m, cost.TotalCost);
    }
}
