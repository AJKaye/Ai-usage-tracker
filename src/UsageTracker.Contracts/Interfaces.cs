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
