using System.Globalization;
using UsageTracker.Contracts;
using UsageTracker.Normalization;

namespace UsageTracker.Ingestion.Otlp;

/// <summary>Reads loosely-typed OTLP/JSON attribute values into the types we need.</summary>
internal static class Attr
{
    public static string? Str(IReadOnlyDictionary<string, object?> a, string k)
        => a.TryGetValue(k, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    public static long? Long(IReadOnlyDictionary<string, object?> a, string k)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return null;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var pd) => (long)pd,
            _ => null,
        };
    }

    public static double? Dbl(IReadOnlyDictionary<string, object?> a, string k)
    {
        if (!a.TryGetValue(k, out var v) || v is null) return null;
        return v switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
    }
}

/// <summary>
/// Maps an OTLP span carrying OpenTelemetry GenAI (<c>gen_ai.*</c>) attributes to
/// the canonical <see cref="Span"/>. Primary dialect. Usage buckets are read raw
/// and reconciled by the provider-aware <see cref="TokenNormalizerRegistry"/>
/// (subset-vs-additive) — the mapper does NOT do token math itself.
/// </summary>
public sealed class GenAiSpanMapper : ISpanMapper
{
    private readonly TokenNormalizerRegistry _normalizers;
    public GenAiSpanMapper(TokenNormalizerRegistry normalizers) => _normalizers = normalizers;

    public bool Handles(string dialect) =>
        dialect is "otlp.gen_ai" or "otlp" or "gen_ai";

    public Span Map(RawIngestEvent raw)
    {
        var a = raw.Attributes;
        var provider = Attr.Str(a, GenAi.ProviderName) ?? Attr.Str(a, GenAi.SystemLegacy);

        var rawUsage = new TokenUsage
        {
            InputTokens = Attr.Long(a, GenAi.InputTokens),
            OutputTokens = Attr.Long(a, GenAi.OutputTokens),
            CacheReadInputTokens = Attr.Long(a, GenAi.CacheReadTokens),
            CacheCreationInputTokens = Attr.Long(a, GenAi.CacheCreationTokens),
            ReasoningOutputTokens = Attr.Long(a, GenAi.ReasoningTokens),
            AudioTokens = Attr.Long(a, GenAi.AudioTokens),
            ImageTokens = Attr.Long(a, GenAi.ImageTokens),
        };
        bool hasUsage = rawUsage.InputTokens is not null || rawUsage.OutputTokens is not null;

        // Coarse granularity (#15) riding OTLP via the aiusage.* extension; default Token
        // preserves the pre-Phase-3 mapping (and every Phase-2 golden).
        var granularity = Attr.Str(a, AiUsage.Granularity)?.ToLowerInvariant() switch
        {
            "credit" => Granularity.Credit,
            "seat" => Granularity.Seat,
            "request" => Granularity.Request,
            _ => Granularity.Token,
        };
        var usage = (hasUsage && granularity == Granularity.Token)
            ? _normalizers.Normalize(provider, rawUsage) : null;

        return new Span
        {
            TenantId = raw.TenantId,
            TraceId = Attr.Str(a, "trace_id") ?? Guid.NewGuid().ToString("n"),
            SpanId = Attr.Str(a, "span_id") ?? Guid.NewGuid().ToString("n"),
            ParentSpanId = Attr.Str(a, "parent_span_id"),
            SessionId = Attr.Str(a, GenAi.ConversationId),
            Kind = MapKind(Attr.Str(a, GenAi.OperationName)),
            Name = Attr.Str(a, GenAi.OperationName),
            Provider = provider,
            RequestModel = Attr.Str(a, GenAi.RequestModel),
            ResponseModel = Attr.Str(a, GenAi.ResponseModel),
            TokenizerId = Attr.Str(a, AiUsage.Tokenizer),
            // pricing selectors (Phase 3 composite key; null/absent = wildcard)
            ServiceTier = Attr.Str(a, AiUsage.ServiceTier),
            IsBatch = a.ContainsKey(AiUsage.Batch) ? Attr.Long(a, AiUsage.Batch) is 1 || string.Equals(Attr.Str(a, AiUsage.Batch), "true", StringComparison.OrdinalIgnoreCase) : null,
            Region = Attr.Str(a, AiUsage.Region),
            DeploymentType = Attr.Str(a, AiUsage.DeploymentType),
            Granularity = granularity,
            RawUsage = hasUsage ? rawUsage : null,
            Usage = usage,
            UnitsConsumed = Attr.Long(a, AiUsage.UnitsConsumed),
            UnitType = Attr.Str(a, AiUsage.UnitType),
            StartTime = raw.ReceivedAt,
            TimeToFirstTokenMs = Attr.Dbl(a, GenAi.TimeToFirstChunkMs),
            UserId = Attr.Str(a, "user_id"),
            TeamId = Attr.Str(a, "team_id"),
            Environment = Attr.Str(a, "environment"),
        };
    }

    // gen_ai.operation.name enum -> canonical SpanKind
    private static SpanKind MapKind(string? op) => op?.ToLowerInvariant() switch
    {
        "chat" or "text_completion" or "generate_content" => SpanKind.Llm,
        "embeddings" => SpanKind.Embedding,
        "execute_tool" => SpanKind.Tool,
        "invoke_agent" or "create_agent" => SpanKind.Agent,
        "invoke_workflow" or "plan" => SpanKind.Chain,
        "retrieval" or "retrieve" => SpanKind.Retriever,
        _ => SpanKind.Llm,   // default: an LLM call
    };
}

/// <summary>
/// Maps a span carrying OpenInference (<c>llm.*</c> / <c>openinference.span.kind</c>)
/// attributes — the second dialect (Phoenix/Arize). OpenInference reports
/// prompt/completion the "subset" way (cache inside prompt), so we route through
/// the normalizer with the resolved provider like any other source.
/// </summary>
public sealed class OpenInferenceSpanMapper : ISpanMapper
{
    private readonly TokenNormalizerRegistry _normalizers;
    public OpenInferenceSpanMapper(TokenNormalizerRegistry normalizers) => _normalizers = normalizers;

    public bool Handles(string dialect) => dialect is "openinference" or "openinference.otlp";

    public Span Map(RawIngestEvent raw)
    {
        var a = raw.Attributes;
        var provider = Attr.Str(a, OpenInference.LlmSystem);

        var rawUsage = new TokenUsage
        {
            InputTokens = Attr.Long(a, OpenInference.PromptTokens),
            OutputTokens = Attr.Long(a, OpenInference.CompletionTokens),
            TotalTokens = Attr.Long(a, OpenInference.TotalTokens),
            CacheReadInputTokens = Attr.Long(a, OpenInference.CacheReadTokens),
            CacheCreationInputTokens = Attr.Long(a, OpenInference.CacheWriteTokens),
            ReasoningOutputTokens = Attr.Long(a, OpenInference.ReasoningTokens),
        };
        bool hasUsage = rawUsage.InputTokens is not null || rawUsage.OutputTokens is not null;
        var usage = hasUsage ? _normalizers.Normalize(provider, rawUsage) : null;

        return new Span
        {
            TenantId = raw.TenantId,
            TraceId = Attr.Str(a, "trace_id") ?? Guid.NewGuid().ToString("n"),
            SpanId = Attr.Str(a, "span_id") ?? Guid.NewGuid().ToString("n"),
            ParentSpanId = Attr.Str(a, "parent_span_id"),
            Kind = MapKind(Attr.Str(a, OpenInference.SpanKind)),
            Provider = provider,
            RequestModel = Attr.Str(a, OpenInference.LlmModelName),
            ResponseModel = Attr.Str(a, OpenInference.LlmModelName),
            Granularity = Granularity.Token,
            RawUsage = hasUsage ? rawUsage : null,
            Usage = usage,
            StartTime = raw.ReceivedAt,
        };
    }

    private static SpanKind MapKind(string? k) => k?.ToUpperInvariant() switch
    {
        "LLM" => SpanKind.Llm,
        "CHAIN" => SpanKind.Chain,
        "TOOL" => SpanKind.Tool,
        "RETRIEVER" => SpanKind.Retriever,
        "EMBEDDING" => SpanKind.Embedding,
        "AGENT" => SpanKind.Agent,
        "RERANKER" => SpanKind.Reranker,
        "GUARDRAIL" => SpanKind.Guardrail,
        "EVALUATOR" => SpanKind.Evaluator,
        _ => SpanKind.Unknown,
    };
}

/// <summary>
/// Routes a <see cref="RawIngestEvent"/> to the first mapper that handles its
/// dialect. Adding a wire dialect = adding an <see cref="ISpanMapper"/>, no change
/// to the receiver.
/// </summary>
public sealed class SpanMapperRegistry
{
    private readonly IReadOnlyList<ISpanMapper> _mappers;
    public SpanMapperRegistry(IEnumerable<ISpanMapper> mappers) => _mappers = mappers.ToList();

    public static SpanMapperRegistry CreateDefault(TokenNormalizerRegistry normalizers) =>
        new(new ISpanMapper[]
        {
            new GenAiSpanMapper(normalizers),
            new OpenInferenceSpanMapper(normalizers),
        });

    public bool CanMap(string dialect) => _mappers.Any(m => m.Handles(dialect));

    public Span Map(RawIngestEvent raw)
    {
        foreach (var m in _mappers)
            if (m.Handles(raw.Dialect))
                return m.Map(raw);
        throw new NotSupportedException($"no span mapper for dialect '{raw.Dialect}'");
    }
}
