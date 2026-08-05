namespace UsageTracker.Contracts;

/// <summary>
/// Reconciles a provider's raw multi-bucket token counts into provider-neutral
/// <see cref="NormalizedUsage"/>. THE correctness keystone: it owns the
/// subset-vs-additive rule (ARCHITECTURE.md §3.4). Implementations are
/// provider-aware and covered by golden tests.
/// </summary>
public interface ITokenNormalizer
{
    /// <summary>True if this normalizer handles the given provider id.</summary>
    bool Handles(string provider);

    /// <summary>Normalize raw usage for a known provider.</summary>
    NormalizedUsage Normalize(string provider, TokenUsage raw);
}

/// <summary>
/// One tier of the 3-tier cost engine (ARCHITECTURE.md §4.1):
/// (1) ingested USD, (2) price-map, (3) tokenize-then-price. Tiers are tried in
/// order; the first that can price the span wins. Each tier is independently
/// replaceable behind this interface.
/// </summary>
public interface ICostTier
{
    /// <summary>Ascending priority; lower runs first.</summary>
    int Order { get; }

    /// <summary>Attempt to cost the span; return null to fall through to the next tier.</summary>
    CostBreakdown? TryCost(Span span);
}

/// <summary>Facade over the tier chain — the thing callers actually use.</summary>
public interface ICostEngine
{
    CostBreakdown? Cost(Span span);
}

/// <summary>
/// Recomputes a stored event's cost two ways (ARCHITECTURE.md §4.1, §5 #14):
/// from its snapshot (stable across catalog changes) or against the live catalog
/// (what it would cost today). Backs the Phase-3 verification: "recomputing a
/// historical day after a price change reproduces the original cost."
/// </summary>
public interface ICostRecomputer
{
    /// <summary>Reproduce the original cost from the rate snapshotted on the span; stable across
    /// live-catalog changes. Returns null when the span carries no cost to replay.</summary>
    CostBreakdown? RecomputeFromSnapshot(Span span);

    /// <summary>Re-cost against the CURRENT live catalog — the "what it costs today" / re-baseline path.</summary>
    CostBreakdown? RecomputeFromLiveCatalog(Span span);
}

/// <summary>A resolved set of rates for one (model, …) composite key.</summary>
public sealed record ModelRate
{
    public required string Model { get; init; }
    public string Currency { get; init; } = "USD";
    public decimal InputPerToken { get; init; }
    public decimal OutputPerToken { get; init; }
    public decimal CacheReadPerToken { get; init; }
    public decimal CacheCreationPerToken { get; init; }
    /// <summary>Defaults to the output rate when unset (reasoning bills as output).</summary>
    public decimal? ReasoningPerToken { get; init; }
    public required string CatalogVersion { get; init; }

    // --- provenance (Increment 1: rate snapshotting / date-effective; default = current behavior) ---
    /// <summary>Which <see cref="IPriceCatalogSource"/> produced this rate ("offline-bundle", …).</summary>
    public string SourceId { get; init; } = "";
    /// <summary>Start of the price window this rate is effective from (#14 date-effective). Null = always.</summary>
    public DateOnly? EffectiveFrom { get; init; }
    /// <summary>Exclusive end of the price window; null = open-ended.</summary>
    public DateOnly? EffectiveTo { get; init; }

    // --- pricing mode + additive rates (Increment 4; defaults preserve token behavior) ---
    /// <summary>How this rate is metered (#8). PerToken (default) uses the token buckets;
    /// PerHour prices <see cref="HourlyRate"/> × elapsed hours regardless of tokens.</summary>
    public PricingMode Mode { get; init; } = PricingMode.PerToken;
    /// <summary>Multiplier applied to the token subtotal (#9 geo/residency/service-tier uplift,
    /// e.g. 1.1 for inference_geo:"us"). Default 1.0 = no change.</summary>
    public decimal Multiplier { get; init; } = 1.0m;
    /// <summary>Per-token rate for audio modality when priced distinctly (#6). Null = fall back to input rate.</summary>
    public decimal? AudioPerToken { get; init; }
    /// <summary>Per-token rate for image modality when priced distinctly (#6). Null = fall back to input rate.</summary>
    public decimal? ImagePerToken { get; init; }
    /// <summary>Hourly rate for PerHour mode (#8 PTU / provisioned throughput / fine-tuned hosting).</summary>
    public decimal? HourlyRate { get; init; }

    // --- composite-key selectors (Increment 3; a null/Any/false dim is a wildcard that matches anything) ---
    /// <summary>Which context-length tier this variant prices (#5). Any = size-independent.</summary>
    public ContextTier ContextTier { get; init; } = ContextTier.Any;
    /// <summary>Total-input-token threshold above which the whole request re-rates to the Long tier (#5).</summary>
    public long? LongContextThresholdTokens { get; init; }
    /// <summary>Which batch state this variant prices (#4). False = the non-batch base rate.</summary>
    public bool IsBatch { get; init; }
    /// <summary>Which service tier this variant prices (#9). Null = any.</summary>
    public string? ServiceTier { get; init; }
    /// <summary>Which region this variant prices (#9/#11). Null = any.</summary>
    public string? Region { get; init; }
    /// <summary>Which deployment type this variant prices (#9). Null = any.</summary>
    public string? DeploymentType { get; init; }
}

/// <summary>
/// A resolved per-unit rate for a coarse (non-Token) surface (ARCHITECTURE.md §5
/// #15 / #8). Parallel to <see cref="ModelRate"/> but single-rate, keyed on
/// <see cref="UnitType"/> and optionally scoped by provider/model. New record —
/// non-breaking.
/// </summary>
public sealed record UnitRate
{
    public required string UnitType { get; init; }        // "ai_unit" | "premium_request" | "copilot_seat" | ...
    public required PricingMode Mode { get; init; }       // PerUnit | PerRequest | PerSeat
    public required decimal PricePerUnit { get; init; }   // USD per one unit
    public string Currency { get; init; } = "USD";
    public string? Provider { get; init; }                // optional scope (null = any)
    public string? Model { get; init; }                   // optional scope (e.g. per-model premium-request rate)
    public required string CatalogVersion { get; init; }
}

/// <summary>
/// The price catalog (ARCHITECTURE.md §4.2). Keyed on more than model alone;
/// this slice keys on model but the signature carries the span so richer
/// composite keying (tier/region/batch) drops in without a breaking change.
/// </summary>
public interface IPriceCatalog
{
    ModelRate? Resolve(Span span);
    string Version { get; }

    /// <summary>Resolve a per-unit rate for a coarse (non-Token) span. Default null keeps
    /// existing implementors non-breaking (ARCHITECTURE.md §5 #15).</summary>
    UnitRate? ResolveUnit(Span span) => null;

    /// <summary>Per-call USD surcharge for a tool type (ARCHITECTURE.md §5 #7, e.g. web_search).
    /// Default null = no surcharge for this catalog.</summary>
    decimal? ToolSurcharge(string toolType) => null;
}

/// <summary>
/// Estimates token counts from raw text for the Tier-3 tokenize-then-price path
/// (ARCHITECTURE.md §4.1 tier 3). A heuristic default ships now; a real BPE
/// tokenizer (tiktoken o200k_base, the Claude tokenizer) plugs in behind this
/// later. <see cref="Id"/> is recorded on the span for tokenizer-drift attribution (#10).
/// </summary>
public interface ITokenizer
{
    string Id { get; }
    long CountTokens(string text);
}

/// <summary>
/// Verifies a signed offline pricing bundle for air-gapped/FedRAMP operation
/// (ARCHITECTURE.md §4.3, D6). Throws if the signature is invalid; returns the
/// bundle's SHA-256 digest on success, so a recompute can prove it used rates
/// from a signature-verified bundle.
/// </summary>
public interface IBundleVerifier
{
    string VerifyAndDigest(byte[] bundleBytes, byte[] signature);
}

/// <summary>
/// Where the catalog's rates come from — live-sync OR a signed offline bundle
/// for air-gap (ARCHITECTURE.md §4.3, D6/FedRAMP). Swapping the source is a
/// config change, not a code change.
/// </summary>
public interface IPriceCatalogSource
{
    IReadOnlyList<ModelRate> Load();
    string SourceId { get; }

    /// <summary>Per-unit rates (credits/seats/requests) from the SAME bundle. Default empty =
    /// non-breaking (ARCHITECTURE.md §5 #15).</summary>
    IReadOnlyList<UnitRate> LoadUnits() => Array.Empty<UnitRate>();

    /// <summary>Per-call tool surcharges (tool type → USD/call) from the SAME bundle
    /// (ARCHITECTURE.md §5 #7). Default empty = non-breaking.</summary>
    IReadOnlyDictionary<string, decimal> LoadToolSurcharges() => new Dictionary<string, decimal>();
}

/// <summary>
/// Persists and queries the canonical Session→Trace→Span data
/// (ARCHITECTURE.md §3.1). Backed by ClickHouse in production; an in-memory
/// implementation satisfies the same contract for local/dev/test (proving the
/// seam). Tenant-scoped on every call — no cross-tenant reads.
/// </summary>
public interface IEventStore
{
    Task AppendAsync(Span span, CancellationToken ct = default);
    Task<Span?> GetAsync(string tenantId, string spanId, CancellationToken ct = default);
    Task<IReadOnlyList<Span>> QueryAsync(SpanQuery query, CancellationToken ct = default);
    Task<UsageSummary> SummarizeAsync(SpanQuery query, CancellationToken ct = default);
}

/// <summary>A tenant-scoped query over spans (kept intentionally small for the slice).</summary>
public sealed record SpanQuery
{
    public required string TenantId { get; init; }
    public string? TraceId { get; init; }
    public string? Provider { get; init; }
    public DateTimeOffset? Since { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>Rolled-up totals for a query window — the seed of the cost/usage dashboards.</summary>
public sealed record UsageSummary
{
    public required int SpanCount { get; init; }
    public required long TotalInputTokens { get; init; }
    public required long TotalOutputTokens { get; init; }
    public required decimal TotalEstimatedCost { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyDictionary<string, decimal> CostByProvider { get; init; }
    public required IReadOnlyDictionary<string, decimal> CostByModel { get; init; }
}
