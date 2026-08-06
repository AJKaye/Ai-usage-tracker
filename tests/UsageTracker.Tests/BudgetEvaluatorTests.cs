using UsageTracker.Contracts;
using UsageTracker.FinOps;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 11 / FinOps control plane — budget evaluation (spend-vs-limit, utilization,
/// run-rate projection, ok/warning/exceeded), with hand-computed goldens.
/// </summary>
public class BudgetEvaluatorTests
{
    private static Span Span(decimal cost, string? team = null, string? provider = "anthropic", string spanId = "s") => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = spanId, Kind = SpanKind.Llm,
        Provider = provider, TeamId = team,
        StartTime = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
        EstimatedCost = new CostBreakdown { TotalCost = cost, Currency = "USD", Components = Array.Empty<CostComponent>(), Tier = "PriceMap" },
    };

    private static Budget Monthly(decimal limit, string dim = "", string? dimValue = null, double warn = 0.8) => new()
    {
        Id = "b1", TenantId = "t", Dimension = dim, DimensionValue = dimValue,
        Limit = limit, Period = BudgetPeriod.Monthly, WarnAtFraction = warn,
    };

    private static readonly DateOnly Aug10 = new(2026, 8, 10);   // day 10 of a 31-day month

    [Fact]
    public void Under_limit_is_ok_with_run_rate_projection()
    {
        // spent 100 over 10 elapsed days → 10/day → projected 10×31 = 310.
        var spans = new[] { Span(60m, spanId: "a"), Span(40m, spanId: "b") };
        var st = BudgetEvaluator.Evaluate(Monthly(1000m), spans, Aug10);

        Assert.Equal(100m, st.SpentToDate);
        Assert.Equal(0.1, st.Utilization, 3);
        Assert.Equal("ok", st.State);
        Assert.Equal(310m, st.ProjectedEndOfPeriod);         // 100/10 × 31
    }

    [Fact]
    public void At_or_above_warn_fraction_is_warning()
    {
        var st = BudgetEvaluator.Evaluate(Monthly(100m, warn: 0.8),
            new[] { Span(85m, spanId: "a") }, Aug10);
        Assert.Equal("warning", st.State);                   // 0.85 ≥ 0.8, < 1.0
        Assert.Equal(0.85, st.Utilization, 3);
    }

    [Fact]
    public void At_or_above_limit_is_exceeded()
    {
        var st = BudgetEvaluator.Evaluate(Monthly(100m),
            new[] { Span(120m, spanId: "a") }, Aug10);
        Assert.Equal("exceeded", st.State);
        Assert.True(st.Utilization >= 1.0);
    }

    [Fact]
    public void Dimension_scoped_budget_only_counts_matching_spans()
    {
        // Budget scoped to team "alpha" — only alpha's spend counts.
        var spans = new[]
        {
            Span(50m, team: "alpha", spanId: "a"),
            Span(90m, team: "beta", spanId: "b"),   // out of scope
            Span(30m, team: "alpha", spanId: "c"),
        };
        var st = BudgetEvaluator.Evaluate(Monthly(1000m, dim: "team", dimValue: "alpha"), spans, Aug10);
        Assert.Equal(80m, st.SpentToDate);                   // 50 + 30, beta excluded
    }

    [Fact]
    public void Daily_period_projects_flat_no_extrapolation()
    {
        var st = BudgetEvaluator.Evaluate(
            new Budget { Id = "d", TenantId = "t", Limit = 50m, Period = BudgetPeriod.Daily },
            new[] { Span(20m, spanId: "a") }, Aug10);
        Assert.Equal(20m, st.SpentToDate);
        Assert.Equal(20m, st.ProjectedEndOfPeriod);          // daily: 1 elapsed / 1 total
    }
}
