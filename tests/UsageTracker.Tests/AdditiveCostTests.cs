using UsageTracker.Contracts;
using UsageTracker.Cost;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 3 / Increment 4 — in-tier additive costs + pricing modes
/// (ARCHITECTURE.md §5 #6 modality, #7 tool surcharges, #8 per-hour PTU,
/// #9 geo/residency multiplier). All computed inside PriceMapCostTier so they
/// stack correctly (the tier chain is first-non-null-wins and cannot add across
/// tiers). Rates hand-computed; catalogs built from inline bundles + the seed.
/// </summary>
public class AdditiveCostTests
{
    private static ICostEngine EngineFor(string json) =>
        TieredCostEngine.CreateDefault(new PriceCatalog(new OfflineBundleCatalogSource(json)));

    private static ICostEngine SeedEngine() =>
        TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));

    private static NormalizedUsage Usage(long input = 0, long output = 0, long audio = 0, long image = 0) => new()
    {
        InputTokens = input, UncachedInputTokens = input,
        CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
        OutputTokens = output, ReasoningOutputTokens = 0,
        AudioTokens = audio, ImageTokens = image,
    };

    private static Span Span(NormalizedUsage? usage, string model = "m",
        IReadOnlyList<ToolCall>? tools = null, DateTimeOffset? start = null, DateTimeOffset? end = null) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Llm,
        Provider = "p", ResponseModel = model, Usage = usage, ToolCalls = tools,
        StartTime = start ?? DateTimeOffset.UnixEpoch, EndTime = end,
    };

    // --- #6 modality: audio priced at its own rate, distinct from text ------------
    [Fact]
    public void Audio_modality_priced_at_its_own_rate()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000025,
            "audio_per_token": 0.0001 }
        ]}
        """;
        // 1000 audio tokens only (disjoint from text buckets).
        var cost = EngineFor(json).Cost(Span(Usage(audio: 1000)))!;
        Assert.Equal(0.10m, cost.TotalCost);                    // 1000 * 1e-4
        Assert.Contains(cost.Components, c => c.Kind == "audio" && c.Cost == 0.10m);
    }

    // --- #7 tool surcharges stack on top of token cost ----------------------------
    [Fact]
    public void Tool_surcharges_stack_on_top_of_token_cost()
    {
        // seed: claude-opus-5 in 5e-6 / out 25e-6; web_search $0.01/call.
        var usage = Usage(input: 1000, output: 500);
        var span = Span(usage, model: "claude-opus-5",
            tools: new[] { new ToolCall("web_search", 3) });
        var cost = SeedEngine().Cost(span)!;

        // token 0.0175 + 3 * 0.01 = 0.0475
        Assert.Equal(0.0475m, cost.TotalCost);
        var tool = Assert.Single(cost.Components, c => c.Kind == "tool:web_search");
        Assert.Equal(0.03m, tool.Cost);
        Assert.Equal(3, tool.Units);
    }

    // --- #8 per-hour PTU: hours × hourly, tokens ignored --------------------------
    [Fact]
    public void PerHour_ptu_prices_by_duration_regardless_of_tokens()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "ptu-deploy", "mode": "per_hour", "hourly_rate": 5.00 }
        ]}
        """;
        var start = DateTimeOffset.Parse("2026-08-10T00:00:00Z");
        var end = start.AddMinutes(150);                        // 2.5 hours
        // Even with token usage present, PerHour ignores it.
        var span = Span(Usage(input: 999999, output: 999999), model: "ptu-deploy", start: start, end: end);
        var cost = EngineFor(json).Cost(span)!;

        Assert.Equal(12.50m, cost.TotalCost);                   // 5.00 * 2.5
        Assert.Contains(cost.Components, c => c.Kind == "provisioned_hours");
    }

    // --- #9 geo multiplier applies to the token subtotal --------------------------
    [Fact]
    public void Geo_multiplier_applies_to_token_subtotal()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000025, "multiplier": 1.1 }
        ]}
        """;
        var cost = EngineFor(json).Cost(Span(Usage(input: 1000, output: 500)))!;
        // base 0.0175 * 1.1 = 0.01925
        Assert.Equal(0.01925m, cost.TotalCost);
        Assert.Contains(cost.Components, c => c.Kind == "geo_multiplier");
    }

    // --- #7 + #9 together: multiplier hits tokens, surcharge stacks after ---------
    [Fact]
    public void Multiplier_applies_to_tokens_then_flat_surcharge_stacks()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000025, "multiplier": 1.1 }
        ], "tool_surcharges": { "web_search": 0.01 } }
        """;
        var span = Span(Usage(input: 1000, output: 500),
            tools: new[] { new ToolCall("web_search", 2) });
        var cost = EngineFor(json).Cost(span)!;
        // tokens 0.0175 * 1.1 = 0.01925 ; + 2 * 0.01 = 0.02 ; total 0.03925
        Assert.Equal(0.03925m, cost.TotalCost);
    }

    // --- components always sum to TotalCost (invariant across the additions) ------
    [Fact]
    public void Components_sum_to_total()
    {
        const string json = """
        { "version": "v", "models": [
          { "model": "m", "input_per_token": 0.000005, "output_per_token": 0.000025,
            "audio_per_token": 0.0001, "multiplier": 1.1 }
        ], "tool_surcharges": { "code_interpreter": 0.03 } }
        """;
        var span = Span(Usage(input: 1000, output: 500, audio: 200),
            tools: new[] { new ToolCall("code_interpreter", 1) });
        var cost = EngineFor(json).Cost(span)!;
        Assert.Equal(cost.TotalCost, cost.Components.Sum(c => c.Cost));
    }
}
