using UsageTracker.Contracts;
using UsageTracker.Cost;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 3 / Increment 3 — composite-key + date-effective price catalog
/// (ARCHITECTURE.md §4.2, §5 #4 batch, #5 context-length whole-request re-rate,
/// #9/#11 region/tier variants, #14 date-effective selection). Rates are
/// hand-computed; catalogs are built from small inline bundles.
/// </summary>
public class CompositeKeyCatalogTests
{
    private static ICostEngine EngineFor(string json) =>
        TieredCostEngine.CreateDefault(new PriceCatalog(new OfflineBundleCatalogSource(json)));

    private static NormalizedUsage InOut(long input, long output) => new()
    {
        InputTokens = input, UncachedInputTokens = input,
        CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
        OutputTokens = output, ReasoningOutputTokens = 0,
    };

    private static Span Span(NormalizedUsage usage, DateTimeOffset? start = null,
        bool? isBatch = null, string? region = null, string model = "m") => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Llm,
        Provider = "p", ResponseModel = model, Usage = usage,
        IsBatch = isBatch, Region = region,
        StartTime = start ?? DateTimeOffset.UnixEpoch,
    };

    // --- #4 batch API ~50% off (a batch variant priced separately) ----------------
    [Fact]
    public void Batch_variant_prices_at_half_and_base_is_unaffected()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000025 },
          { "model": "m", "input_per_token": 0.0000025, "output_per_token": 0.0000125, "is_batch": true }
        ]}
        """;
        var engine = EngineFor(json);

        var batch = engine.Cost(Span(InOut(1000, 500), isBatch: true))!;
        Assert.Equal(0.00875m, batch.TotalCost);   // 1000*2.5e-6 + 500*12.5e-6 = 0.0025 + 0.00625

        var nonBatch = engine.Cost(Span(InOut(1000, 500), isBatch: false))!;
        Assert.Equal(0.0175m, nonBatch.TotalCost);  // base rate — exactly double the batch cost
    }

    // --- #5 context-length: crossing the threshold re-rates the WHOLE request ------
    [Fact]
    public void Long_context_rerates_the_whole_request_not_just_the_overflow()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000003, "output_per_token": 0.000015,
            "context_tier": "standard", "long_context_threshold": 200000 },
          { "model": "m", "input_per_token": 0.000006, "output_per_token": 0.000030,
            "context_tier": "long", "long_context_threshold": 200000 }
        ]}
        """;
        var engine = EngineFor(json);

        // 199,000 ≤ 200,000 → standard rate on the whole request.
        var under = engine.Cost(Span(InOut(199_000, 500)))!;
        Assert.Equal(0.6045m, under.TotalCost);     // 199000*3e-6 + 500*15e-6 = 0.597 + 0.0075

        // 201,000 > 200,000 → the ENTIRE request re-rates at the long rate.
        var over = engine.Cost(Span(InOut(201_000, 500)))!;
        Assert.Equal(1.221m, over.TotalCost);        // 201000*6e-6 + 500*30e-6 = 1.206 + 0.015
    }

    // --- #9 / #11 region variant: same model + tokens, different region → diff cost -
    [Fact]
    public void Region_variant_selects_the_matching_regional_rate()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000003, "output_per_token": 0.000015, "region": "us-east-1" },
          { "model": "m", "input_per_token": 0.0000036, "output_per_token": 0.000018, "region": "eu-west-1" }
        ]}
        """;
        var engine = EngineFor(json);

        var us = engine.Cost(Span(InOut(1000, 500), region: "us-east-1"))!;
        Assert.Equal(0.0105m, us.TotalCost);         // 1000*3e-6 + 500*15e-6

        var eu = engine.Cost(Span(InOut(1000, 500), region: "eu-west-1"))!;
        Assert.Equal(0.0126m, eu.TotalCost);         // 1000*3.6e-6 + 500*18e-6
    }

    // --- a more-specific variant wins over the wildcard base ----------------------
    [Fact]
    public void Most_specific_variant_wins_over_base()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000003, "output_per_token": 0.000015 },
          { "model": "m", "input_per_token": 0.000009, "output_per_token": 0.000045, "region": "gov-cloud" }
        ]}
        """;
        var engine = EngineFor(json);

        // region matches the specific row → it wins over the base wildcard.
        var gov = engine.Cost(Span(InOut(1000, 0), region: "gov-cloud"))!;
        Assert.Equal(0.009m, gov.TotalCost);         // 1000*9e-6 (specific), not 1000*3e-6 (base)

        // a region with no specific row falls back to the base wildcard.
        var other = engine.Cost(Span(InOut(1000, 0), region: "ap-south-1"))!;
        Assert.Equal(0.003m, other.TotalCost);       // 1000*3e-6 (base)
    }

    // --- #14 date-effective: resolution picks the rate in effect at event time -----
    [Fact]
    public void Date_effective_resolution_picks_rate_in_effect_at_event_time()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000025,
            "effective_from": "2026-01-01", "effective_to": "2026-09-01" },
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000030,
            "effective_from": "2026-09-01" }
        ]}
        """;
        var engine = EngineFor(json);

        var august = engine.Cost(Span(InOut(1000, 500), start: DateTimeOffset.Parse("2026-08-15T00:00:00Z")))!;
        Assert.Equal(0.0175m, august.TotalCost);     // old rate: 0.005 + 500*25e-6

        var september = engine.Cost(Span(InOut(1000, 500), start: DateTimeOffset.Parse("2026-09-15T00:00:00Z")))!;
        Assert.Equal(0.0200m, september.TotalCost);  // new rate: 0.005 + 500*30e-6
    }
}
