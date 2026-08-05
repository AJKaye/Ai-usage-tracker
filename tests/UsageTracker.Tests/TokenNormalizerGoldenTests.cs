using UsageTracker.Contracts;
using UsageTracker.Normalization;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// GOLDEN SUITE #1 — the subset-vs-additive token rule (ARCHITECTURE.md §3.4).
/// This is the #1 correctness keystone: getting the direction wrong mis-bills
/// every cached request. These cases pin both families against hand-computed
/// expectations.
/// </summary>
public class TokenNormalizerGoldenTests
{
    private readonly TokenNormalizerRegistry _reg = TokenNormalizerRegistry.CreateDefault();

    [Theory]
    [InlineData("openai")]
    [InlineData("gcp.gemini")]
    [InlineData("azure.ai.openai")]
    public void Subset_family_treats_cache_as_inside_input(string provider)
    {
        // OpenAI/Google: input_tokens is the WHOLE prompt; cache is a subset of it.
        var raw = new TokenUsage
        {
            InputTokens = 1000,               // whole prompt
            CacheReadInputTokens = 600,        // already inside the 1000
            CacheCreationInputTokens = 0,
            OutputTokens = 300,
            ReasoningOutputTokens = 120,       // already inside the 300
        };

        var n = _reg.Normalize(provider, raw);

        Assert.Equal(1000, n.InputTokens);            // NOT 1600 — no double count
        Assert.Equal(400, n.UncachedInputTokens);      // 1000 - 600
        Assert.Equal(600, n.CacheReadInputTokens);
        Assert.Equal(300, n.OutputTokens);             // reasoning stays inside output
        Assert.Equal(120, n.ReasoningOutputTokens);
        Assert.Equal(1300, n.TotalTokens);             // 1000 + 300
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("aws.bedrock")]
    public void Additive_family_adds_cache_back_onto_input(string provider)
    {
        // Anthropic/Bedrock: input_tokens is the UNCACHED remainder only.
        var raw = new TokenUsage
        {
            InputTokens = 400,                 // uncached remainder ONLY
            CacheReadInputTokens = 600,         // separate — must be added back
            CacheCreationInputTokens = 200,     // separate — must be added back
            OutputTokens = 300,
            ReasoningOutputTokens = 80,
        };

        var n = _reg.Normalize(provider, raw);

        Assert.Equal(1200, n.InputTokens);            // 400 + 600 + 200 (added back)
        Assert.Equal(400, n.UncachedInputTokens);
        Assert.Equal(600, n.CacheReadInputTokens);
        Assert.Equal(200, n.CacheCreationInputTokens);
        Assert.Equal(300, n.OutputTokens);
        Assert.Equal(1500, n.TotalTokens);             // 1200 + 300
    }

    [Fact]
    public void Same_raw_numbers_yield_different_input_totals_across_families()
    {
        // The crux: identical wire numbers mean different true prompt sizes.
        var raw = new TokenUsage { InputTokens = 500, CacheReadInputTokens = 500, OutputTokens = 100 };

        var subset = _reg.Normalize("openai", raw);
        var additive = _reg.Normalize("anthropic", raw);

        Assert.Equal(500, subset.InputTokens);    // cache already inside
        Assert.Equal(1000, additive.InputTokens);  // cache added back
        Assert.NotEqual(subset.InputTokens, additive.InputTokens);
    }

    [Fact]
    public void Unknown_provider_falls_back_without_throwing()
    {
        var n = _reg.Normalize("some.new.provider", new TokenUsage { InputTokens = 100, OutputTokens = 50 });
        Assert.Equal(100, n.InputTokens);
        Assert.Equal(50, n.OutputTokens);
    }
}
