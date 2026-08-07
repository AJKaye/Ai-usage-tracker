using System.Collections.Concurrent;
using UsageTracker.Contracts;

namespace UsageTracker.Orchestration;

/// <summary>
/// Embedded, tenant-scoped <see cref="IWorkflowStore"/> — the zero-infra impl (mirrors
/// <c>InMemoryBudgetStore</c>). Keyed on (tenant, workflowId); a durable store satisfies the
/// same contract in the scale tier.
/// </summary>
public sealed class InMemoryWorkflowStore : IWorkflowStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkflowDefinition>> _byTenant = new();

    public Task UpsertAsync(WorkflowDefinition wf, CancellationToken ct = default)
    {
        _byTenant.GetOrAdd(wf.TenantId, _ => new())[wf.Id] = wf;
        return Task.CompletedTask;
    }

    public Task<WorkflowDefinition?> GetAsync(string tenantId, string workflowId, CancellationToken ct = default)
    {
        WorkflowDefinition? wf = _byTenant.TryGetValue(tenantId, out var t) && t.TryGetValue(workflowId, out var w)
            ? w : null;
        return Task.FromResult(wf);
    }

    public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<WorkflowDefinition> list = _byTenant.TryGetValue(tenantId, out var t)
            ? t.Values.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList() : Array.Empty<WorkflowDefinition>();
        return Task.FromResult(list);
    }

    public Task DeleteAsync(string tenantId, string workflowId, CancellationToken ct = default)
    {
        if (_byTenant.TryGetValue(tenantId, out var t)) t.TryRemove(workflowId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Embedded, tenant-scoped <see cref="IRunStore"/>. Keyed on (tenant, runId); the polling
/// overlay reads a run's live per-node state from here. Bounded is unnecessary for the slice
/// (a durable store handles retention later).
/// </summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkflowRun>> _byTenant = new();

    public Task UpsertAsync(WorkflowRun run, CancellationToken ct = default)
    {
        _byTenant.GetOrAdd(run.TenantId, _ => new())[run.RunId] = run;
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        WorkflowRun? run = _byTenant.TryGetValue(tenantId, out var t) && t.TryGetValue(runId, out var r)
            ? r : null;
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, string? workflowId, CancellationToken ct = default)
    {
        IReadOnlyList<WorkflowRun> list = _byTenant.TryGetValue(tenantId, out var t)
            ? t.Values
                .Where(r => workflowId is null || r.WorkflowId == workflowId)
                .OrderByDescending(r => r.StartedAt).ToList()
            : Array.Empty<WorkflowRun>();
        return Task.FromResult(list);
    }
}
