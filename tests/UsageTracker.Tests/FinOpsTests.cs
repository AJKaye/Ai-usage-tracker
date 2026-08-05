using UsageTracker.Contracts;
using UsageTracker.FinOps;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 6 / Increment 1 — tag-free allocation + unit economics (ARCHITECTURE.md
/// §6.2/§6.3). The load-bearing exit criterion: allocation sums to 100% of spend
/// with NO upstream tags present.
/// </summary>
public class FinOpsTests
{
    private static NormalizedUsage Usage(long input, long output) => new()
    {
        InputTokens = input, UncachedInputTokens = input,
        CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
        OutputTokens = output, ReasoningOutputTokens = 0,
    };

    private static Span S(decimal cost, string? team = null, string? user = null,
        string? model = null, SpanKind kind = SpanKind.Llm, NormalizedUsage? usage = null,
        IReadOnlyDictionary<string, string>? metadata = null) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = Guid.NewGuid().ToString("n"), Kind = kind,
        Provider = "anthropic", ResponseModel = model, TeamId = team, UserId = user,
        Usage = usage, Metadata = metadata,
        StartTime = DateTimeOffset.UnixEpoch,
        EstimatedCost = new CostBreakdown
        {
            TotalCost = cost, Currency = "USD",
            Components = Array.Empty<CostComponent>(), Tier = "PriceMap",
        },
    };

    // --- the exit criterion: 100% of spend allocated, even with tags absent --------
    [Fact]
    public void Allocation_sums_to_100_percent_of_spend_with_no_tags()
    {
        var spans = new[]
        {
            S(0.10m, team: "alpha"),
            S(0.20m, team: "alpha"),
            S(0.30m, team: "beta"),
            S(0.40m),                    // NO team → must land in (unattributed), not vanish
        };
        var buckets = new DimensionAllocationStrategy("team").Allocate(spans);

        decimal totalSpend = spans.Sum(s => s.EstimatedCost!.TotalCost);
        decimal allocated = buckets.Sum(b => b.Cost);
        Assert.Equal(1.00m, totalSpend);
        Assert.Equal(totalSpend, allocated);          // 100% — nothing lost

        Assert.Equal(0.30m, Assert.Single(buckets, b => b.Key == "alpha").Cost);   // 0.10 + 0.20
        Assert.Equal(0.30m, Assert.Single(buckets, b => b.Key == "beta").Cost);
        Assert.Equal(0.40m, Assert.Single(buckets, b => b.Key == DimensionAllocationStrategy.Unattributed).Cost);
        // sorted by cost desc: the 0.40 unattributed bucket leads
        Assert.Equal(DimensionAllocationStrategy.Unattributed, buckets[0].Key);
    }

    [Fact]
    public void Allocation_by_metadata_feature_dimension_is_tag_free()
    {
        var spans = new[]
        {
            S(0.50m, metadata: new Dictionary<string, string> { ["feature"] = "search" }),
            S(0.25m, metadata: new Dictionary<string, string> { ["feature"] = "chat" }),
            S(0.25m, metadata: new Dictionary<string, string> { ["feature"] = "search" }),
        };
        var buckets = new DimensionAllocationStrategy("feature").Allocate(spans);
        Assert.Equal(0.75m, Assert.Single(buckets, b => b.Key == "search").Cost);
        Assert.Equal(2, Assert.Single(buckets, b => b.Key == "search").SpanCount);
        Assert.Equal(1.00m, buckets.Sum(b => b.Cost));
    }

    // --- unit economics: hand-computed -------------------------------------------
    [Fact]
    public void Cost_per_token_and_inference_and_outcome_hand_computed()
    {
        var spans = new[]
        {
            S(0.02m, usage: Usage(600, 400)),   // 1000 tokens
            S(0.03m, usage: Usage(700, 300)),   // 1000 tokens
        };
        // total cost 0.05, total tokens 2000, inferences 2 (both Llm), outcomes 5
        Assert.Equal(0.000025m, new CostPerTokenMetric().Compute(spans, 0));      // 0.05 / 2000
        Assert.Equal(0.025m, new CostPerInferenceMetric().Compute(spans, 0));     // 0.05 / 2
        Assert.Equal(0.01m, new CostPerOutcomeMetric().Compute(spans, 5));        // 0.05 / 5
    }

    [Fact]
    public void Unit_metrics_are_zero_safe_on_empty_denominators()
    {
        var none = Array.Empty<Span>();
        Assert.Equal(0m, new CostPerTokenMetric().Compute(none, 0));
        Assert.Equal(0m, new CostPerOutcomeMetric().Compute(new[] { S(1m) }, 0));   // outcome denom 0
    }

    // --- efficiency roll-up -------------------------------------------------------
    [Fact]
    public void Efficiency_summary_computes_cache_hit_and_error_rate()
    {
        var start = DateTimeOffset.UnixEpoch;
        Span WithTiming(NormalizedUsage u, double ttft, string status) => new()
        {
            TenantId = "t", TraceId = "tr", SpanId = Guid.NewGuid().ToString("n"), Kind = SpanKind.Llm,
            Provider = "anthropic", Usage = u, Status = status,
            StartTime = start, EndTime = start.AddMilliseconds(200), TimeToFirstTokenMs = ttft,
        };
        var spans = new[]
        {
            WithTiming(new NormalizedUsage { InputTokens = 1000, UncachedInputTokens = 400, CacheReadInputTokens = 600, CacheCreationInputTokens = 0, OutputTokens = 100, ReasoningOutputTokens = 0 }, 50, "ok"),
            WithTiming(new NormalizedUsage { InputTokens = 1000, UncachedInputTokens = 1000, CacheReadInputTokens = 0, CacheCreationInputTokens = 0, OutputTokens = 100, ReasoningOutputTokens = 0 }, 150, "error"),
        };
        var eff = EfficiencyCalculator.Compute(spans);

        Assert.Equal(2, eff.SpanCount);
        Assert.Equal(200, eff.AvgDurationMs);
        Assert.Equal(100, eff.AvgTimeToFirstTokenMs);        // (50 + 150) / 2
        Assert.Equal(0.30, eff.CacheHitRate, 3);             // 600 cache / 2000 input
        Assert.Equal(0.50, eff.ErrorRate, 3);                // 1 of 2 non-ok
    }
}
