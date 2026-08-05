using System.Text.Json.Serialization;

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
/// How a catalog rate is metered (ARCHITECTURE.md §5 #8). The token path uses
/// <see cref="ModelRate"/>; the coarse modes use <see cref="UnitRate"/> priced by
/// <c>CoarseUnitCostTier</c>. Additive enum — non-breaking.
/// </summary>
public enum PricingMode
{
    PerToken,    // token buckets × ModelRate (the default token path)
    PerUnit,     // credits / "AI units" — UnitsConsumed × PricePerUnit
    PerRequest,  // premium requests — request count × PricePerUnit
    PerSeat,     // seat licences — seats × PricePerUnit per period
    PerHour      // PTU / provisioned throughput — hours × HourlyRate regardless of tokens
}

/// <summary>
/// Context-length pricing tier (ARCHITECTURE.md §5 #5). Once a prompt crosses a
/// model's threshold, the higher rate applies to the WHOLE request. A rate variant
/// declares which tier it prices; <c>Any</c> matches regardless of prompt size.
/// </summary>
public enum ContextTier
{
    Any,
    Standard,
    Long
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

/// <summary>
/// A count of per-call tool invocations on a span, for the tool surcharges that
/// stack on top of token cost (ARCHITECTURE.md §5 #7: web search ~$10/1k calls,
/// file search, code interpreter). <see cref="ToolType"/> matches a catalog
/// surcharge key (e.g. "web_search", "code_interpreter").
/// </summary>
public sealed record ToolCall(string ToolType, int Count);

/// <summary>
/// The exact pricing state used to cost ONE event, snapshotted so a later
/// recompute reproduces the original figure even after the live catalog changes
/// (ARCHITECTURE.md §4.1 "store the rate, not just the cost"; §5 #14). Snapshots
/// the WHOLE resolved <see cref="ModelRate"/>, so any rate dimension added later
/// (pricing mode, context-tier, geo multiplier, modality) is captured here with
/// no further change. All fields beyond Rate/provenance are optional so later
/// phases fill them without a breaking change.
/// </summary>
public sealed record RateSnapshot
{
    /// <summary>The rate row that was resolved and used — the source of truth for replay.</summary>
    public required ModelRate Rate { get; init; }
    /// <summary><see cref="IPriceCatalogSource.SourceId"/> the rate came from ("offline-bundle", "litellm", …).</summary>
    public required string CatalogSourceId { get; init; }
    /// <summary>Catalog version/date stamp (mirrors <see cref="ModelRate.CatalogVersion"/>; hoisted for query/audit).</summary>
    public required string CatalogVersion { get; init; }
    /// <summary>UTC instant the cost was computed (≈ ingest time) — the pin for "rate in effect at event time".</summary>
    public required DateTimeOffset CapturedAt { get; init; }
    /// <summary>Start of the price window this rate belongs to, when the source stamps it.</summary>
    public DateOnly? EffectiveFrom { get; init; }
    /// <summary>Composite-key selectors in force when the rate was resolved (service_tier/region/batch/context-tier/…).</summary>
    public IReadOnlyDictionary<string, string>? RateKey { get; init; }
    /// <summary>#10 tokenizer drift: the tokenizer/model generation the counts were computed against.</summary>
    public string? TokenizerId { get; init; }
    /// <summary>SHA-256 of the signed offline pricing bundle the rate came from, when known (D6/FedRAMP).</summary>
    public string? CatalogDigest { get; init; }
}

/// <summary>The estimated cost of one event, decomposed into line items.</summary>
public sealed record CostBreakdown
{
    public required decimal TotalCost { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<CostComponent> Components { get; init; }
    /// <summary>Which cost tier produced this (IngestedUsd | PriceMap | CoarseUnit | Tokenized | Unpriced).</summary>
    public required string Tier { get; init; }
    /// <summary>Version/date stamp of the price catalog used.</summary>
    public string? CatalogVersion { get; init; }
    /// <summary>
    /// The rate snapshot that produced this cost (ARCHITECTURE.md §4.1). Present for
    /// rate-derived tiers (PriceMap); null for IngestedUsd/Unpriced, which carry no
    /// replayable rate. Optional so pre-snapshot events still deserialize.
    /// </summary>
    public RateSnapshot? RateSnapshot { get; init; }
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
    /// <summary>#10 tokenizer drift: the tokenizer/model generation the token counts were produced by
    /// (e.g. "claude-tokenizer.4-8", "o200k_base"). Snapshotted so a cost change is attributable to
    /// tokenizer drift vs a price move. Set by the Tier-3 tokenizer, or carried from the wire.</summary>
    public string? TokenizerId { get; init; }

    // --- pricing selectors (Increment 3: composite catalog key; all optional) ---
    /// <summary>Service tier in force (e.g. "standard"|"priority"|"flex"|"scale"), a catalog key dim (#9).</summary>
    public string? ServiceTier { get; init; }
    /// <summary>Whether this request went through a batch API (~50% off; ARCHITECTURE.md §5 #4).</summary>
    public bool? IsBatch { get; init; }
    /// <summary>Cloud region — third-party model pricing varies by region (#9/#11).</summary>
    public string? Region { get; init; }
    /// <summary>Deployment type (e.g. "global-standard"|"data-zone"|"ptu"), a catalog key dim (#9).</summary>
    public string? DeploymentType { get; init; }
    /// <summary>Per-call tool invocations that add surcharges on top of token cost (#7).</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    // --- usage (as reported) + normalized + cost ---
    public Granularity Granularity { get; init; } = Granularity.Token;
    public TokenUsage? RawUsage { get; init; }
    public NormalizedUsage? Usage { get; init; }
    /// <summary>
    /// TRANSIENT opt-in text used ONLY by the Tier-3 tokenize-then-price estimator when
    /// no token counts were reported. <see cref="JsonIgnoreAttribute"/> so it is NEVER
    /// persisted (content is PII-classified and captured separately, opt-in — §3.2).
    /// Null in the normal path where the provider already reported usage.
    /// </summary>
    [JsonIgnore]
    public string? EstimationText { get; init; }
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
