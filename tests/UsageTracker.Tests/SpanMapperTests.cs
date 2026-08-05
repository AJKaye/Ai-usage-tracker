using UsageTracker.Contracts;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Normalization;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 2 golden suite: the wire→canonical span mappers, over both dialects.
/// Confirms the mapper carries provider/model/kind AND that the subset-vs-additive
/// token rule survives the mapping (the mapper defers to the normalizer, so an
/// Anthropic OTLP span must reconstruct true input just like the direct path).
/// </summary>
public class SpanMapperTests
{
    private readonly SpanMapperRegistry _reg =
        SpanMapperRegistry.CreateDefault(TokenNormalizerRegistry.CreateDefault());

    private static RawIngestEvent Raw(string dialect, Dictionary<string, object?> attrs) => new()
    {
        TenantId = "t", Dialect = dialect, Attributes = attrs, ReceivedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void GenAi_openai_subset_mapping_keeps_cache_inside_input()
    {
        var span = _reg.Map(Raw("otlp.gen_ai", new()
        {
            ["gen_ai.provider.name"] = "openai",
            ["gen_ai.operation.name"] = "chat",
            ["gen_ai.response.model"] = "gpt-5.6",
            ["gen_ai.usage.input_tokens"] = 1000L,
            ["gen_ai.usage.cache_read.input_tokens"] = 600L,
            ["gen_ai.usage.output_tokens"] = 300L,
            ["span_id"] = "s1", ["trace_id"] = "tr1",
        }));

        Assert.Equal("openai", span.Provider);
        Assert.Equal("gpt-5.6", span.ResponseModel);
        Assert.Equal(SpanKind.Llm, span.Kind);
        Assert.Equal(1000, span.Usage!.InputTokens);          // subset: cache inside input
        Assert.Equal(400, span.Usage!.UncachedInputTokens);   // 1000 - 600
        Assert.Equal("s1", span.SpanId);
    }

    [Fact]
    public void GenAi_anthropic_additive_mapping_adds_cache_back()
    {
        var span = _reg.Map(Raw("otlp.gen_ai", new()
        {
            ["gen_ai.provider.name"] = "anthropic",
            ["gen_ai.response.model"] = "claude-opus-5",
            ["gen_ai.usage.input_tokens"] = 200L,          // uncached remainder
            ["gen_ai.usage.cache_read.input_tokens"] = 600L,
            ["gen_ai.usage.cache_creation.input_tokens"] = 200L,
            ["gen_ai.usage.output_tokens"] = 500L,
        }));

        Assert.Equal(1000, span.Usage!.InputTokens);   // additive: 200+600+200
        Assert.Equal(200, span.Usage!.UncachedInputTokens);
    }

    [Fact]
    public void GenAi_operation_name_maps_to_span_kind()
    {
        SpanKind Kind(string op) => _reg.Map(Raw("otlp.gen_ai", new()
        {
            ["gen_ai.provider.name"] = "openai", ["gen_ai.operation.name"] = op,
        })).Kind;

        Assert.Equal(SpanKind.Embedding, Kind("embeddings"));
        Assert.Equal(SpanKind.Tool, Kind("execute_tool"));
        Assert.Equal(SpanKind.Agent, Kind("invoke_agent"));
        Assert.Equal(SpanKind.Retriever, Kind("retrieval"));
    }

    [Fact]
    public void OpenInference_dialect_maps_llm_attrs_and_kind()
    {
        var span = _reg.Map(Raw("openinference", new()
        {
            ["openinference.span.kind"] = "RETRIEVER",
            ["llm.system"] = "openai",
            ["llm.model_name"] = "gpt-5.6",
            ["llm.token_count.prompt"] = 800L,
            ["llm.token_count.completion"] = 200L,
            ["llm.token_count.prompt_details.cache_read"] = 300L,
        }));

        Assert.Equal(SpanKind.Retriever, span.Kind);
        Assert.Equal("openai", span.Provider);
        Assert.Equal(800, span.Usage!.InputTokens);           // subset family
        Assert.Equal(500, span.Usage!.UncachedInputTokens);   // 800 - 300
    }

    [Fact]
    public void Unknown_dialect_is_reported_not_silently_mapped()
    {
        Assert.False(_reg.CanMap("some.future.dialect"));
        Assert.Throws<NotSupportedException>(() =>
            _reg.Map(Raw("some.future.dialect", new() { ["x"] = 1L })));
    }
}

/// <summary>
/// Phase 2: the OTLP/HTTP JSON envelope parser — the real OpenTelemetry trace
/// wire shape (resourceSpans→scopeSpans→spans, AnyValue attributes).
/// </summary>
public class OtlpTraceParserTests
{
    [Fact]
    public void Parses_nested_envelope_flattens_attrs_and_merges_resource()
    {
        // Minimal but real OTLP JSON: one resource span, one scope span, two spans.
        // intValue is a STRING per the OTLP JSON mapping (int64 as string).
        const string json = """
        {
          "resourceSpans": [{
            "resource": { "attributes": [
              { "key": "deployment.environment", "value": { "stringValue": "prod" } }
            ]},
            "scopeSpans": [{
              "spans": [
                {
                  "traceId": "abc", "spanId": "span-a", "name": "chat",
                  "attributes": [
                    { "key": "gen_ai.provider.name", "value": { "stringValue": "anthropic" } },
                    { "key": "gen_ai.response.model", "value": { "stringValue": "claude-opus-5" } },
                    { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "200" } },
                    { "key": "gen_ai.usage.cache_read.input_tokens", "value": { "intValue": "600" } },
                    { "key": "gen_ai.usage.cache_creation.input_tokens", "value": { "intValue": "200" } },
                    { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "500" } }
                  ]
                },
                {
                  "traceId": "abc", "spanId": "span-b",
                  "attributes": [
                    { "key": "gen_ai.provider.name", "value": { "stringValue": "openai" } },
                    { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "100" } },
                    { "key": "gen_ai.usage.output_tokens", "value": { "intValue": "50" } }
                  ]
                }
              ]
            }]
          }]
        }
        """;

        var events = OtlpTraceParser.Parse(json, "tenant-1", DateTimeOffset.UnixEpoch);

        Assert.Equal(2, events.Count);
        var a = events[0];
        Assert.Equal("tenant-1", a.TenantId);
        Assert.Equal("otlp.gen_ai", a.Dialect);
        Assert.Equal("span-a", a.Attributes["span_id"]);
        Assert.Equal("anthropic", a.Attributes["gen_ai.provider.name"]);
        Assert.Equal(200L, a.Attributes["gen_ai.usage.input_tokens"]);   // intValue string → long
        Assert.Equal("prod", a.Attributes["deployment.environment"]);     // resource attr merged in
    }

    [Fact]
    public void Empty_or_missing_resourceSpans_yields_no_events()
    {
        Assert.Empty(OtlpTraceParser.Parse("{}", "t", DateTimeOffset.UnixEpoch));
        Assert.Empty(OtlpTraceParser.Parse("""{"resourceSpans":[]}""", "t", DateTimeOffset.UnixEpoch));
    }
}
