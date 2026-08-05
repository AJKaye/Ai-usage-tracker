namespace UsageTracker.Contracts;

/// <summary>
/// Relational state (catalog, tenancy, identity, entitlement) — the Postgres
/// seam (ARCHITECTURE.md §8.1). Deliberately generic: modules own their tables
/// behind this, honoring "no shared mutable DB access across modules" (§5 rule 1).
/// Every operation is tenant-scoped; the production impl enforces Postgres RLS.
/// </summary>
public interface IRelationalStore
{
    Task<T?> GetAsync<T>(string tenantId, string collection, string id, CancellationToken ct = default) where T : class;
    Task UpsertAsync<T>(string tenantId, string collection, string id, T value, CancellationToken ct = default) where T : class;
    Task<IReadOnlyList<T>> ListAsync<T>(string tenantId, string collection, CancellationToken ct = default) where T : class;
    Task DeleteAsync(string tenantId, string collection, string id, CancellationToken ct = default);
}

/// <summary>
/// The async pipeline bus — the Kafka seam (ARCHITECTURE.md §8.1). Producers
/// publish to a topic; consumer groups subscribe. At-least-once; consumers dedup
/// by span/event id. An in-process bus satisfies the contract for the slice.
/// </summary>
public interface IStreamBus
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default);
    Task SubscribeAsync<T>(string topic, string consumerGroup, Func<T, CancellationToken, Task> handler, CancellationToken ct = default);
}

/// <summary>
/// Pulls realized (billed) cost from a provider's reporting/billing API — the
/// reconciliation ground truth (ARCHITECTURE.md §4/§8.3 step 2). One per
/// provider (OpenAI Costs, Anthropic cost_report, Azure Cost Mgmt, GCP BigQuery,
/// AWS CUR). Optional: air-gapped deployments run without any connector.
/// </summary>
public interface IBillingConnector
{
    string Provider { get; }
    /// <summary>Realized cost rows for the window; resumable via checkpointing in the impl.</summary>
    Task<IReadOnlyList<RealizedCost>> PullAsync(string tenantId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>A realized (authoritative) cost line from a provider billing feed.</summary>
public sealed record RealizedCost
{
    public required string Provider { get; init; }
    public required DateOnly Day { get; init; }
    public string? Model { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? CostType { get; init; }   // tokens / web_search / code_execution / session_usage
}

/// <summary>
/// Reconciles estimated per-event cost against pulled realized cost and stores
/// the delta at an appropriate grain (ARCHITECTURE.md §4.1: estimated vs
/// reconciled are separate layers; surface the delta).
/// </summary>
public interface IReconciler
{
    Task<ReconciliationResult> ReconcileAsync(string tenantId, DateOnly day, CancellationToken ct = default);
}

/// <summary>
/// Estimated-vs-realized reconciliation for one tenant-day. The delta is
/// <see cref="RealizedTotal"/> − <see cref="EstimatedTotal"/>; a per-provider
/// breakdown makes it explainable. When no billing connector is configured
/// (air-gap, D6), <see cref="ReconciledAgainstBilling"/> is false and the estimate
/// stands alone — surfaced, not silently zero.
/// </summary>
public sealed record ReconciliationResult
{
    public required DateOnly Day { get; init; }
    public required decimal EstimatedTotal { get; init; }
    public required decimal RealizedTotal { get; init; }
    public decimal Delta => RealizedTotal - EstimatedTotal;
    public required string Currency { get; init; }
    /// <summary>False in air-gap / no-connector mode: RealizedTotal is not authoritative, the estimate stands.</summary>
    public bool ReconciledAgainstBilling { get; init; } = true;
    /// <summary>Per-provider estimated/realized/delta lines — the explainable breakdown.</summary>
    public IReadOnlyList<ProviderReconciliation> ByProvider { get; init; } = Array.Empty<ProviderReconciliation>();
}

/// <summary>One provider's estimated-vs-realized line within a day's reconciliation.</summary>
public sealed record ProviderReconciliation
{
    public required string Provider { get; init; }
    public required decimal Estimated { get; init; }
    public required decimal Realized { get; init; }
    public decimal Delta => Realized - Estimated;
}

/// <summary>
/// Persists reconciliation results at the tenant-day grain (ARCHITECTURE.md §4:
/// store the delta). The Phase-4 "reconciliation views/materializations" seam —
/// backed by ClickHouse in the scale tier, an embedded store in the solo tier;
/// swapping is a config change, and both pass the same conformance tests.
/// </summary>
public interface IReconciliationStore
{
    Task SaveAsync(string tenantId, ReconciliationResult result, CancellationToken ct = default);
    Task<ReconciliationResult?> GetAsync(string tenantId, DateOnly day, CancellationToken ct = default);
}
