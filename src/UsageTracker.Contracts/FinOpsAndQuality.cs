namespace UsageTracker.Contracts;

/// <summary>
/// Allocates a set of spans' cost across attribution dimensions — including the
/// frontier tag-free / dimension-based allocation (ARCHITECTURE.md §6.2), which
/// attributes 100% of a shared endpoint's cost to team/user/agent/session from
/// captured span dimensions without requiring upstream tags.
/// </summary>
public interface IAllocationStrategy
{
    string Name { get; }
    /// <summary>Dimension key each bucket is keyed by (e.g. "team", "user", "feature").</summary>
    string Dimension { get; }
    IReadOnlyList<AllocationBucket> Allocate(IReadOnlyList<Span> spans);
}

public sealed record AllocationBucket(string Key, decimal Cost, string Currency, int SpanCount);

/// <summary>
/// A unit-economics metric — cost per token/inference/call up to cost-per-outcome
/// (ARCHITECTURE.md §6.3). Pluggable so "cost per case deflected" etc. are added
/// without touching the engine.
/// </summary>
public interface IUnitMetric
{
    string Name { get; }
    /// <summary>Compute the metric over a span set + a denominator (e.g. #outcomes).</summary>
    decimal Compute(IReadOnlyList<Span> spans, long denominator);
    string Unit { get; }
}

/// <summary>
/// Ingests an externally-computed quality/eval score and attaches it to a span or
/// trace (ARCHITECTURE.md §6.3: be the score AGGREGATOR, not the eval engine).
/// Framework-agnostic; the product never owns the judge.
/// </summary>
public interface IScoreSink
{
    Task AttachAsync(Score score, CancellationToken ct = default);
    Task<IReadOnlyList<Score>> GetForSpanAsync(string tenantId, string spanId, CancellationToken ct = default);
}

/// <summary>A named score value (numeric | categorical | boolean) bound to a span/trace.</summary>
public sealed record Score
{
    public required string TenantId { get; init; }
    public required string TargetId { get; init; }   // span or trace id
    public required string Name { get; init; }
    public double? Numeric { get; init; }
    public string? Category { get; init; }
    public bool? Boolean { get; init; }
    public string? Source { get; init; }             // which framework/evaluator produced it
    public DateTimeOffset At { get; init; }
}
