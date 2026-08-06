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

// ============================================================================
//  FinOps Control Plane — budgets, alerts, anomaly detection, forecasting.
//  Turns the tracker from a passive reporting hub into an active control plane
//  (ARCHITECTURE.md §6). All estimated-cost-layer; budget-vs-*reconciled* follows
//  when the cloud billing connectors land.
// ============================================================================

/// <summary>Budget window — the period a spend limit applies over.</summary>
public enum BudgetPeriod { Daily, Monthly }

/// <summary>
/// A spend limit for a tenant, optionally scoped to one allocation dimension
/// (whole-tenant if <see cref="Dimension"/> is empty; else team/model/provider/
/// environment/… — the same dimensions <c>DimensionAllocationStrategy</c> keys on).
/// A null <see cref="DimensionValue"/> means "each value of the dimension shares one
/// limit" (aggregate); a set value scopes the budget to that one value.
/// </summary>
public sealed record Budget
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public string Dimension { get; init; } = "";        // "" = whole tenant
    public string? DimensionValue { get; init; }        // null = aggregate across the dimension
    public required decimal Limit { get; init; }
    public string Currency { get; init; } = "USD";
    public BudgetPeriod Period { get; init; } = BudgetPeriod.Monthly;
    /// <summary>Utilization fraction at which a "warning" alert fires (default 80%).</summary>
    public double WarnAtFraction { get; init; } = 0.8;
}

/// <summary>
/// A budget evaluated against actual spend for the current period (mirrors the
/// estimated-vs-actual shape of <see cref="ReconciliationResult"/>).
/// </summary>
public sealed record BudgetStatus
{
    public required Budget Budget { get; init; }
    public required decimal SpentToDate { get; init; }
    public required decimal Limit { get; init; }
    public required double Utilization { get; init; }            // SpentToDate / Limit
    public required decimal ProjectedEndOfPeriod { get; init; }  // run-rate projection
    public required string State { get; init; }                  // ok | warning | exceeded
}

/// <summary>Stores tenant budget definitions (mirrors <see cref="IReconciliationStore"/>).</summary>
public interface IBudgetStore
{
    Task UpsertAsync(Budget budget, CancellationToken ct = default);
    Task<IReadOnlyList<Budget>> ListAsync(string tenantId, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string budgetId, CancellationToken ct = default);
}

/// <summary>An in-app alert — also the payload an optional webhook <see cref="INotifier"/> sends.</summary>
public sealed record Alert
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Kind { get; init; }        // budget_warning | budget_exceeded | cost_anomaly
    public required string Message { get; init; }
    public decimal? Value { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Reference { get; init; }           // budget id / day / span id
}

/// <summary>
/// The in-app alert feed (works in every profile, incl. air-gapped). In-memory now;
/// a durable store satisfies the same contract later.
/// </summary>
public interface IAlertSink
{
    Task RaiseAsync(Alert alert, CancellationToken ct = default);
    Task<IReadOnlyList<Alert>> RecentAsync(string tenantId, int limit, CancellationToken ct = default);
}

/// <summary>
/// Optional OUTBOUND alert delivery (Slack/Teams/generic webhook). An implementation
/// MUST gate on <see cref="IEgressGuard"/> so it fails closed under air-gap. Absent a
/// configured notifier, alerts live only in the in-app feed.
/// </summary>
public interface INotifier
{
    Task NotifyAsync(Alert alert, CancellationToken ct = default);
}

/// <summary>A detected cost anomaly: a day whose spend deviates from its trailing baseline.</summary>
public sealed record AnomalyResult
{
    public required DateOnly Day { get; init; }
    public required decimal Cost { get; init; }
    public required decimal BaselineMean { get; init; }
    public required decimal ExpectedUpperBound { get; init; }   // mean + k·stddev
    /// <summary>Std-devs above the baseline mean; null when the baseline is perfectly flat
    /// (zero variance → z undefined, yet any increase is anomalous by definition).</summary>
    public required double? ZScore { get; init; }
    public required string Currency { get; init; }
}
