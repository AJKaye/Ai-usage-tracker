namespace UsageTracker.Contracts;

/// <summary>
/// The durable enqueue seam on the hot path (ARCHITECTURE.md §2). The ingestion
/// gateway validates + authenticates, then hands the canonical span to a channel
/// that must accept-and-enqueue in &lt;10ms p99 (SLO §8) — heavy work (cost,
/// reconciliation) happens downstream. Backed by Kafka in production; an
/// in-process channel satisfies the same contract for the slice.
/// </summary>
public interface IIngestChannel
{
    /// <summary>Durably enqueue; must be idempotent by <see cref="Span.SpanId"/>.</summary>
    Task EnqueueAsync(Span span, CancellationToken ct = default);
}

/// <summary>
/// A raw usage event as received off the wire, before mapping to the canonical
/// model. Carries the source dialect so <see cref="ISpanMapper"/> can pick the
/// right mapping (OTel gen_ai.* vs OpenInference vs a proxy capture).
/// </summary>
public sealed record RawIngestEvent
{
    public required string TenantId { get; init; }
    /// <summary>e.g. "otlp.gen_ai", "openinference", "proxy.openai", "cloudevent".</summary>
    public required string Dialect { get; init; }
    public required IReadOnlyDictionary<string, object?> Attributes { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>
/// Maps a <see cref="RawIngestEvent"/> of a known dialect into a canonical
/// <see cref="Span"/> (ARCHITECTURE.md §3.3). One mapper per wire dialect; adding
/// a dialect = adding a mapper, no change to the gateway.
/// </summary>
public interface ISpanMapper
{
    bool Handles(string dialect);
    Span Map(RawIngestEvent raw);
}

/// <summary>
/// The zero-instrumentation proxy archetype (ARCHITECTURE.md §2). An
/// OpenAI-compatible backend forwards a request to the upstream provider and
/// returns the response, emitting a canonical span for the round-trip. Kept a
/// contract so proxy vs direct-OTLP is a deployment choice, not a code fork.
/// </summary>
public interface IProxyBackend
{
    /// <summary>Provider id this backend proxies (e.g. "openai", "anthropic").</summary>
    string Provider { get; }

    /// <summary>Forward the raw HTTP body upstream; return upstream bytes + the span it produced.</summary>
    Task<ProxyResult> ForwardAsync(string tenantId, ReadOnlyMemory<byte> requestBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}

public sealed record ProxyResult(ReadOnlyMemory<byte> ResponseBody, int StatusCode, Span Span);

/// <summary>
/// The pull-adapter archetype for closed/coarse surfaces — Cursor, Claude Code,
/// Copilot, UiPath, provider billing APIs (ARCHITECTURE.md §7). Adapters are the
/// primary plugin type; a third party adds a surface by shipping an
/// <see cref="IUsageAdapter"/> against the contract version, with no core change.
/// </summary>
public interface IUsageAdapter
{
    /// <summary>Stable id, e.g. "cursor", "github-copilot", "uipath".</summary>
    string SourceId { get; }

    /// <summary>
    /// Pull usage for the window since <paramref name="since"/> for a tenant and
    /// yield canonical spans. Coarse surfaces set <see cref="Span.Granularity"/>
    /// to credit/seat/request rather than token.
    /// </summary>
    IAsyncEnumerable<Span> PullAsync(string tenantId, DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>How often an adapter is polled (adapters are scheduled, not real-time).</summary>
public interface IAdapterSchedule
{
    string SourceId { get; }
    TimeSpan Interval { get; }
}
