using UsageTracker.Contracts;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Normalization;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 3 / Increment 6 — wire flow: the aiusage.* extension attributes and
/// modality tokens carry the new pricing dimensions from OTLP onto the canonical
/// Span, while a plain gen_ai.* span is unchanged (Token, no dims) so every
/// Phase-2 golden still holds.
/// </summary>
public class Phase3WireFlowTests
{
    private readonly SpanMapperRegistry _reg =
        SpanMapperRegistry.CreateDefault(TokenNormalizerRegistry.CreateDefault());

    private static RawIngestEvent Raw(Dictionary<string, object?> attrs) => new()
    {
        TenantId = "t", Dialect = "otlp.gen_ai", Attributes = attrs, ReceivedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Pricing_selectors_flow_from_aiusage_extension_keys()
    {
        var span = _reg.Map(Raw(new()
        {
            ["gen_ai.provider.name"] = "anthropic",
            ["gen_ai.response.model"] = "claude-opus-5",
            ["gen_ai.usage.input_tokens"] = 200L,
            ["gen_ai.usage.output_tokens"] = 100L,
            ["aiusage.service_tier"] = "priority",
            ["aiusage.batch"] = "true",
            ["aiusage.region"] = "us-east-1",
            ["aiusage.deployment_type"] = "global-standard",
            ["aiusage.tokenizer"] = "claude-tokenizer.4-8",
        }));

        Assert.Equal("priority", span.ServiceTier);
        Assert.True(span.IsBatch);
        Assert.Equal("us-east-1", span.Region);
        Assert.Equal("global-standard", span.DeploymentType);
        Assert.Equal("claude-tokenizer.4-8", span.TokenizerId);
        Assert.Equal(Granularity.Token, span.Granularity);
    }

    [Fact]
    public void Coarse_granularity_rides_otlp_via_aiusage_keys()
    {
        var span = _reg.Map(Raw(new()
        {
            ["gen_ai.provider.name"] = "uipath",
            ["aiusage.granularity"] = "credit",
            ["aiusage.units_consumed"] = 2L,
            ["aiusage.unit_type"] = "ai_unit",
        }));

        Assert.Equal(Granularity.Credit, span.Granularity);
        Assert.Equal(2, span.UnitsConsumed);
        Assert.Equal("ai_unit", span.UnitType);
        Assert.Null(span.Usage);   // coarse → no token normalization
    }

    [Fact]
    public void Modality_tokens_flow_into_raw_usage()
    {
        var span = _reg.Map(Raw(new()
        {
            ["gen_ai.provider.name"] = "openai",
            ["gen_ai.response.model"] = "gpt-5.6",
            ["gen_ai.usage.input_tokens"] = 1000L,
            ["gen_ai.usage.audio.tokens"] = 500L,
        }));

        Assert.Equal(500, span.RawUsage!.AudioTokens);
    }

    [Fact]
    public void Plain_genai_span_is_unchanged_token_no_dims()
    {
        var span = _reg.Map(Raw(new()
        {
            ["gen_ai.provider.name"] = "anthropic",
            ["gen_ai.response.model"] = "claude-opus-5",
            ["gen_ai.usage.input_tokens"] = 200L,
            ["gen_ai.usage.cache_read.input_tokens"] = 600L,
            ["gen_ai.usage.cache_creation.input_tokens"] = 200L,
            ["gen_ai.usage.output_tokens"] = 500L,
        }));

        // Pre-Phase-3 behavior preserved: Token, all new selectors null.
        Assert.Equal(Granularity.Token, span.Granularity);
        Assert.Null(span.ServiceTier);
        Assert.Null(span.IsBatch);
        Assert.Null(span.Region);
        Assert.Null(span.TokenizerId);
        Assert.Equal(1000, span.Usage!.InputTokens);   // additive normalization intact
    }
}
