namespace UsageTracker.Contracts;

/// <summary>
/// The span kind — first-class and extensible (ARCHITECTURE.md §3.1). Uses
/// Phoenix's 9-kind superset so the agentic era (agent/tool/retriever/…) is
/// covered without a schema change.
/// </summary>
public enum SpanKind
{
    Llm,
    Agent,
    Tool,
    Chain,
    Retriever,
    Embedding,
    Reranker,
    Guardrail,
    Evaluator,
    Unknown
}

/// <summary>
/// How a usage event is metered — so the cost engine knows whether it is pricing
/// tokens, credits, seats, or requests (ARCHITECTURE.md §1: the fidelity spectrum).
/// Coarse surfaces (RPA "AI units", IDE seats) land in the same model as rich
/// token data via this discriminator.
/// </summary>
public enum Granularity
{
    Token,
    Credit,
    Seat,
    Request
}

/// <summary>
/// Multi-bucket token counts (ARCHITECTURE.md §3.2 / §3.4). The buckets are
/// stored as the provider reports them; <see cref="ITokenNormalizer"/> owns the
/// subset-vs-additive rule that reconciles them into a single canonical total.
/// A null means "not reported", which is distinct from zero.
/// </summary>
public sealed record TokenUsage
{
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long? ReasoningOutputTokens { get; init; }
    public long? CacheReadInputTokens { get; init; }
    public long? CacheCreationInputTokens { get; init; }
    public long? AudioTokens { get; init; }
    public long? ImageTokens { get; init; }
}

/// <summary>
/// The canonical, provider-neutral token counts after normalization. Every field
/// is a true, additive line item — safe to price directly, no double counting.
/// </summary>
public sealed record NormalizedUsage
{
    /// <summary>Full prompt size: uncached + cache-read + cache-creation.</summary>
    public required long InputTokens { get; init; }
    /// <summary>The portion of input billed at the base (uncached) rate.</summary>
    public required long UncachedInputTokens { get; init; }
    public required long CacheReadInputTokens { get; init; }
    public required long CacheCreationInputTokens { get; init; }
    /// <summary>Full output incl. reasoning (reasoning bills at output rate).</summary>
    public required long OutputTokens { get; init; }
    public required long ReasoningOutputTokens { get; init; }
    public long AudioTokens { get; init; }
    public long ImageTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// A cost line item with the rate that produced it snapshotted alongside
/// (ARCHITECTURE.md §4.1: store the rate, not just the cost, so historical
/// recompute is stable).
/// </summary>
public sealed record CostComponent(string Kind, long Units, decimal RatePerUnit, decimal Cost);

/// <summary>The estimated cost of one event, decomposed into line items.</summary>
public sealed record CostBreakdown
{
    public required decimal TotalCost { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<CostComponent> Components { get; init; }
    /// <summary>Which cost tier produced this (IngestedUsd | PriceMap | Tokenized).</summary>
    public required string Tier { get; init; }
    /// <summary>Version/date stamp of the price catalog used.</summary>
    public string? CatalogVersion { get; init; }
}

/// <summary>
/// One unit of work in the canonical hierarchy Session → Trace → Span
/// (ARCHITECTURE.md §3). This is the superset record every ingestion archetype
/// normalizes into. Content (prompts/responses) is intentionally NOT here — it
/// is opt-in and PII-classified, captured separately.
/// </summary>
public sealed record Span
{
    // --- identity / tree ---
    public required string TenantId { get; init; }
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public string? SessionId { get; init; }
    public required SpanKind Kind { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }

    // --- model ---
    public string? Provider { get; init; }
    public string? RequestModel { get; init; }
    public string? ResponseModel { get; init; }

    // --- usage (as reported) + normalized + cost ---
    public Granularity Granularity { get; init; } = Granularity.Token;
    public TokenUsage? RawUsage { get; init; }
    public NormalizedUsage? Usage { get; init; }
    /// <summary>For coarse surfaces: units consumed when Granularity != Token.</summary>
    public long? UnitsConsumed { get; init; }
    public string? UnitType { get; init; }
    public CostBreakdown? EstimatedCost { get; init; }

    // --- timing ---
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public double? TimeToFirstTokenMs { get; init; }

    // --- attribution ---
    public string? UserId { get; init; }
    public string? TeamId { get; init; }
    public string? Environment { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
