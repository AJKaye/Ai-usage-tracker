namespace UsageTracker.Ingestion.Otlp;

/// <summary>
/// Pinned OpenTelemetry GenAI semantic-convention attribute keys (gen_ai.*).
/// The spec is "Development" stability and WILL churn — pinning every key here,
/// behind the mapper, means a convention bump is a single-file change, never a
/// scatter across the codebase (ARCHITECTURE.md §3.3, DEVELOPMENT_PLAN Phase 2:
/// "pin the semconv version and isolate it behind a mapper").
///
/// Pinned to the 1.29-era gen_ai.* naming used across the project docs.
/// </summary>
internal static class GenAi
{
    public const string PinnedSemConvVersion = "gen_ai/1.29";

    // model / operation
    public const string ProviderName = "gen_ai.provider.name";
    public const string SystemLegacy = "gen_ai.system";          // older alias for provider
    public const string OperationName = "gen_ai.operation.name";
    public const string RequestModel = "gen_ai.request.model";
    public const string ResponseModel = "gen_ai.response.model";
    public const string ConversationId = "gen_ai.conversation.id";

    // usage (multi-bucket)
    public const string InputTokens = "gen_ai.usage.input_tokens";
    public const string OutputTokens = "gen_ai.usage.output_tokens";
    public const string CacheReadTokens = "gen_ai.usage.cache_read.input_tokens";
    public const string CacheCreationTokens = "gen_ai.usage.cache_creation.input_tokens";
    public const string ReasoningTokens = "gen_ai.usage.reasoning.output_tokens";

    // usage (modality — priced distinctly per §5 #6)
    public const string AudioTokens = "gen_ai.usage.audio.tokens";
    public const string ImageTokens = "gen_ai.usage.image.tokens";

    // timing
    public const string TimeToFirstChunkMs = "gen_ai.response.time_to_first_chunk_ms";
}

/// <summary>
/// Tracker-owned extension keys for pricing dimensions that OpenTelemetry
/// <c>gen_ai.*</c> has no attribute for (service tier, batch, region, deployment,
/// tool-call counts, tokenizer id, coarse granularity/units). Namespaced
/// <c>aiusage.*</c> so they never collide with a future standard key, and pinned
/// here like every other key so a producer emits the agreed names (§5 #4/#7/#8/#9/#15,
/// #10). All optional; absence preserves the pre-Phase-3 mapping behavior.
/// </summary>
internal static class AiUsage
{
    // composite-key pricing selectors
    public const string ServiceTier = "aiusage.service_tier";
    public const string Batch = "aiusage.batch";
    public const string Region = "aiusage.region";
    public const string DeploymentType = "aiusage.deployment_type";
    // tokenizer-drift attribution (#10)
    public const string Tokenizer = "aiusage.tokenizer";
    // coarse surfaces (#15) — non-token granularity riding OTLP
    public const string Granularity = "aiusage.granularity";      // "credit"|"seat"|"request"
    public const string UnitsConsumed = "aiusage.units_consumed";
    public const string UnitType = "aiusage.unit_type";
}

/// <summary>
/// Pinned OpenInference attribute keys — the second wire dialect (Phoenix/Arize).
/// Kept separate from gen_ai.* so the two conventions can drift independently.
/// </summary>
internal static class OpenInference
{
    public const string SpanKind = "openinference.span.kind";     // LLM|CHAIN|TOOL|RETRIEVER|...
    public const string LlmSystem = "llm.system";                  // provider
    public const string LlmModelName = "llm.model_name";
    public const string PromptTokens = "llm.token_count.prompt";
    public const string CompletionTokens = "llm.token_count.completion";
    public const string TotalTokens = "llm.token_count.total";
    public const string CacheReadTokens = "llm.token_count.prompt_details.cache_read";
    public const string CacheWriteTokens = "llm.token_count.prompt_details.cache_write";
    public const string ReasoningTokens = "llm.token_count.completion_details.reasoning";
}
