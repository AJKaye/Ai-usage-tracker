using System.Text.Json.Serialization;
using UsageTracker.Contracts;

namespace UsageTracker.Ingestion.Api;

/// <summary>
/// Wire DTO for an inbound usage event, shaped after the OpenTelemetry GenAI
/// (`gen_ai.*`) span conventions but accepted as flat JSON for ergonomic
/// integration (ARCHITECTURE.md §3.3 / §7). A full OTLP receiver is the Phase-2
/// production surface; this JSON endpoint is the same mapping over a simpler
/// transport so the slice is exercisable with curl.
/// </summary>
public sealed record IngestEventDto
{
    // gen_ai.* core
    [JsonPropertyName("gen_ai.provider.name")] public string? Provider { get; init; }
    [JsonPropertyName("gen_ai.operation.name")] public string? Operation { get; init; }
    [JsonPropertyName("gen_ai.request.model")] public string? RequestModel { get; init; }
    [JsonPropertyName("gen_ai.response.model")] public string? ResponseModel { get; init; }

    // usage buckets (gen_ai.usage.*)
    [JsonPropertyName("gen_ai.usage.input_tokens")] public long? InputTokens { get; init; }
    [JsonPropertyName("gen_ai.usage.output_tokens")] public long? OutputTokens { get; init; }
    [JsonPropertyName("gen_ai.usage.cache_read.input_tokens")] public long? CacheReadInputTokens { get; init; }
    [JsonPropertyName("gen_ai.usage.cache_creation.input_tokens")] public long? CacheCreationInputTokens { get; init; }
    [JsonPropertyName("gen_ai.usage.reasoning.output_tokens")] public long? ReasoningOutputTokens { get; init; }

    // identity / tree
    [JsonPropertyName("trace_id")] public string? TraceId { get; init; }
    [JsonPropertyName("span_id")] public string? SpanId { get; init; }
    [JsonPropertyName("parent_span_id")] public string? ParentSpanId { get; init; }
    [JsonPropertyName("gen_ai.conversation.id")] public string? SessionId { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    // timing
    [JsonPropertyName("start_time")] public DateTimeOffset? StartTime { get; init; }
    [JsonPropertyName("end_time")] public DateTimeOffset? EndTime { get; init; }
    [JsonPropertyName("gen_ai.response.time_to_first_chunk_ms")] public double? TimeToFirstTokenMs { get; init; }

    // attribution
    [JsonPropertyName("user_id")] public string? UserId { get; init; }
    [JsonPropertyName("team_id")] public string? TeamId { get; init; }
    [JsonPropertyName("environment")] public string? Environment { get; init; }
    [JsonPropertyName("metadata")] public Dictionary<string, string>? Metadata { get; init; }

    // coarse-surface (RPA units / seats) — optional
    [JsonPropertyName("granularity")] public string? Granularity { get; init; }
    [JsonPropertyName("units_consumed")] public long? UnitsConsumed { get; init; }
    [JsonPropertyName("unit_type")] public string? UnitType { get; init; }

    public TokenUsage ToRawUsage() => new()
    {
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        CacheReadInputTokens = CacheReadInputTokens,
        CacheCreationInputTokens = CacheCreationInputTokens,
        ReasoningOutputTokens = ReasoningOutputTokens,
    };

    public static SpanKind ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "llm" or "chat" or "generate_content" or "text_completion" => SpanKind.Llm,
        "agent" or "invoke_agent" or "create_agent" => SpanKind.Agent,
        "tool" or "execute_tool" => SpanKind.Tool,
        "chain" or "invoke_workflow" or "plan" => SpanKind.Chain,
        "retriever" or "retrieval" => SpanKind.Retriever,
        "embedding" or "embeddings" => SpanKind.Embedding,
        "reranker" => SpanKind.Reranker,
        "guardrail" => SpanKind.Guardrail,
        "evaluator" => SpanKind.Evaluator,
        _ => SpanKind.Unknown,
    };

    public static Granularity ParseGranularity(string? g) => g?.ToLowerInvariant() switch
    {
        "credit" => Contracts.Granularity.Credit,
        "seat" => Contracts.Granularity.Seat,
        "request" => Contracts.Granularity.Request,
        _ => Contracts.Granularity.Token,
    };
}
