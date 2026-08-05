using UsageTracker.Contracts;
using UsageTracker.Cost;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// GOLDEN SUITE #2 — the 3-tier cost engine + gotchas (ARCHITECTURE.md §4/§5).
/// Prices are hand-computed from the seed catalog (seed-2026-08-04).
/// </summary>
public class CostEngineGoldenTests
{
    private static ICostEngine Engine()
    {
        var catalog = new PriceCatalog(OfflineBundleCatalogSource.Seed());
        return TieredCostEngine.CreateDefault(catalog);
    }

    private static Span SpanWith(string model, NormalizedUsage usage, string provider = "anthropic") => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Llm,
        Provider = provider, ResponseModel = model, Usage = usage,
        StartTime = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Tier2_prices_each_token_bucket_at_its_own_rate()
    {
        // claude-opus-5 seed rates: in 5e-6, out 25e-6, cacheRead 5e-7, cacheCreate 6.25e-6
        var usage = new NormalizedUsage
        {
            InputTokens = 1000, UncachedInputTokens = 1000,
            CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
            OutputTokens = 500, ReasoningOutputTokens = 0,
        };
        var cost = Engine().Cost(SpanWith("claude-opus-5", usage));

        Assert.NotNull(cost);
        Assert.Equal("PriceMap", cost!.Tier);
        // 1000*5e-6 + 500*25e-6 = 0.005 + 0.0125 = 0.0175
        Assert.Equal(0.0175m, cost.TotalCost);
        Assert.Equal("seed-2026-08-04", cost.CatalogVersion);
    }

    [Fact]
    public void Cache_read_and_creation_priced_separately_from_base_input()
    {
        // gotcha #2: one request, multiple input rates simultaneously.
        var usage = new NormalizedUsage
        {
            InputTokens = 1000, UncachedInputTokens = 200,
            CacheReadInputTokens = 600, CacheCreationInputTokens = 200,
            OutputTokens = 0, ReasoningOutputTokens = 0,
        };
        var cost = Engine().Cost(SpanWith("claude-opus-5", usage))!;

        // 200*5e-6 + 600*5e-7 + 200*6.25e-6 = 0.001 + 0.0003 + 0.00125 = 0.00255
        Assert.Equal(0.00255m, cost.TotalCost);
        Assert.Contains(cost.Components, c => c.Kind == "input_uncached");
        Assert.Contains(cost.Components, c => c.Kind == "cache_read");
        Assert.Contains(cost.Components, c => c.Kind == "cache_creation");
    }

    [Fact]
    public void Reasoning_tokens_billed_at_output_rate_and_not_double_counted()
    {
        // gotcha #3: reasoning is inside OutputTokens; price it once.
        var usage = new NormalizedUsage
        {
            InputTokens = 0, UncachedInputTokens = 0,
            CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
            OutputTokens = 1000, ReasoningOutputTokens = 400,
        };
        var cost = Engine().Cost(SpanWith("claude-opus-5", usage))!;

        // (1000-400)*25e-6 output + 400*25e-6 reasoning = 0.015 + 0.010 = 0.025
        // == 1000*25e-6 exactly (no double count).
        Assert.Equal(0.025m, cost.TotalCost);
    }

    [Fact]
    public void Tier1_ingested_usd_wins_over_price_map()
    {
        var usage = new NormalizedUsage
        {
            InputTokens = 1000, UncachedInputTokens = 1000, CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0, OutputTokens = 500, ReasoningOutputTokens = 0,
        };
        var span = SpanWith("claude-opus-5", usage) with
        {
            EstimatedCost = new CostBreakdown
            {
                TotalCost = 9.99m, Currency = "USD",
                Components = Array.Empty<CostComponent>(), Tier = "IngestedUsd",
            },
        };
        var cost = Engine().Cost(span)!;
        Assert.Equal("IngestedUsd", cost.Tier);
        Assert.Equal(9.99m, cost.TotalCost);   // not the 0.0175 the price map would compute
    }

    [Fact]
    public void Unknown_model_falls_through_to_unpriced_not_wrong_price()
    {
        var usage = new NormalizedUsage
        {
            InputTokens = 1000, UncachedInputTokens = 1000, CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0, OutputTokens = 500, ReasoningOutputTokens = 0,
        };
        var cost = Engine().Cost(SpanWith("model-that-does-not-exist", usage))!;
        Assert.Equal("Unpriced", cost.Tier);
        Assert.Equal(0m, cost.TotalCost);   // honest zero, not a fabricated price
    }
}
