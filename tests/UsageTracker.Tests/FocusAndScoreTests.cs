using UsageTracker.Contracts;
using UsageTracker.FinOps;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 6 / Increment 2 — FOCUS-column projection (ARCHITECTURE.md §6.1) and the
/// score aggregator (§6.3). A token span and a coarse unit span both map to the same
/// FOCUS column set; externally-computed scores attach by target id, tenant-scoped.
/// </summary>
public class FocusProjectionTests
{
    [Fact]
    public void Token_span_projects_to_focus_row_with_token_consumption()
    {
        var span = new Span
        {
            TenantId = "acme", TraceId = "tr", SpanId = "sp-1", Kind = SpanKind.Llm,
            Provider = "anthropic", ResponseModel = "claude-opus-5",
            Usage = new NormalizedUsage
            {
                InputTokens = 1000, UncachedInputTokens = 1000, CacheReadInputTokens = 0,
                CacheCreationInputTokens = 0, OutputTokens = 500, ReasoningOutputTokens = 0,
            },
            StartTime = DateTimeOffset.UnixEpoch,
            EstimatedCost = new CostBreakdown
            {
                TotalCost = 0.0175m, Currency = "USD",
                Components = Array.Empty<CostComponent>(), Tier = "PriceMap",
            },
        };
        var row = FocusProjection.Project(span);

        Assert.Equal("acme", row.BillingAccountId);
        Assert.Equal("anthropic", row.ProviderName);
        Assert.Equal("claude-opus-5", row.ServiceName);
        Assert.Equal("sp-1", row.ResourceId);
        Assert.Equal("Usage", row.ChargeCategory);
        Assert.Equal(1500m, row.ConsumedQuantity);       // total tokens 1000 + 500
        Assert.Equal("tokens", row.ConsumedUnit);
        Assert.Equal(0.0175m, row.BilledCost);
        Assert.Equal(0.0175m, row.ListCost);
        Assert.Equal(0.0175m, row.EffectiveCost);
        Assert.Equal("USD", row.BillingCurrency);
    }

    [Fact]
    public void Coarse_unit_span_projects_with_unit_consumption()
    {
        var span = new Span
        {
            TenantId = "acme", TraceId = "tr", SpanId = "sp-2", Kind = SpanKind.Tool,
            Provider = "uipath", Granularity = Granularity.Credit,
            UnitsConsumed = 2, UnitType = "ai_unit",
            StartTime = DateTimeOffset.UnixEpoch,
            EstimatedCost = new CostBreakdown
            {
                TotalCost = 0.40m, Currency = "USD",
                Components = Array.Empty<CostComponent>(), Tier = "CoarseUnit",
            },
        };
        var row = FocusProjection.Project(span);

        Assert.Equal(2m, row.ConsumedQuantity);
        Assert.Equal("ai_unit", row.ConsumedUnit);        // virtual-currency unit, not "tokens"
        Assert.Equal(0.40m, row.BilledCost);
    }
}

public class ScoreSinkTests
{
    private static Score Numeric(string tenant, string target, string name, double v, string source) => new()
    {
        TenantId = tenant, TargetId = target, Name = name, Numeric = v, Source = source,
        At = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Score_round_trips_onto_a_span()
    {
        var sink = new InMemoryScoreSink();
        await sink.AttachAsync(Numeric("t", "sp-1", "helpfulness", 0.92, "ragas"));
        await sink.AttachAsync(Numeric("t", "sp-1", "toxicity", 0.01, "openai-mod"));

        var scores = await sink.GetForSpanAsync("t", "sp-1");
        Assert.Equal(2, scores.Count);
        Assert.Contains(scores, s => s.Name == "helpfulness" && s.Numeric == 0.92 && s.Source == "ragas");
    }

    [Fact]
    public async Task Scores_are_tenant_scoped()
    {
        var sink = new InMemoryScoreSink();
        await sink.AttachAsync(Numeric("tenant-a", "sp-shared", "q", 1.0, "x"));

        Assert.Single(await sink.GetForSpanAsync("tenant-a", "sp-shared"));
        Assert.Empty(await sink.GetForSpanAsync("tenant-b", "sp-shared"));   // no cross-tenant read
    }
}
