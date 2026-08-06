using System.Collections.Concurrent;
using System.Net.Http.Json;
using UsageTracker.Contracts;

namespace UsageTracker.FinOps;

/// <summary>
/// Embedded, tenant-scoped <see cref="IBudgetStore"/> — the zero-infra impl (mirrors
/// InMemoryReconciliationStore). Keyed on (tenant, budgetId); a durable store
/// satisfies the same contract in the scale tier.
/// </summary>
public sealed class InMemoryBudgetStore : IBudgetStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Budget>> _byTenant = new();

    public Task UpsertAsync(Budget b, CancellationToken ct = default)
    {
        _byTenant.GetOrAdd(b.TenantId, _ => new())[b.Id] = b;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Budget>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<Budget> list = _byTenant.TryGetValue(tenantId, out var t)
            ? t.Values.ToList() : Array.Empty<Budget>();
        return Task.FromResult(list);
    }

    public Task DeleteAsync(string tenantId, string budgetId, CancellationToken ct = default)
    {
        if (_byTenant.TryGetValue(tenantId, out var t)) t.TryRemove(budgetId, out _);
        return Task.CompletedTask;
    }

    /// <summary>Tenants that currently have at least one budget — the scan service's work list.</summary>
    public IReadOnlyList<string> TenantsWithBudgets() =>
        _byTenant.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key).ToList();
}

/// <summary>
/// Embedded, tenant-scoped in-app alert feed — works in every profile incl.
/// air-gapped (no outbound). Keeps a bounded recent history per tenant.
/// </summary>
public sealed class InMemoryAlertSink : IAlertSink
{
    private const int MaxPerTenant = 500;
    private readonly ConcurrentDictionary<string, List<Alert>> _byTenant = new();
    private readonly object _gate = new();

    public Task RaiseAsync(Alert a, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = _byTenant.GetOrAdd(a.TenantId, _ => new());
            list.Add(a);
            if (list.Count > MaxPerTenant) list.RemoveRange(0, list.Count - MaxPerTenant);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Alert>> RecentAsync(string tenantId, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Alert> recent = _byTenant.TryGetValue(tenantId, out var list)
                ? list.AsEnumerable().Reverse().Take(limit).ToList()   // newest first
                : Array.Empty<Alert>();
            return Task.FromResult(recent);
        }
    }
}

/// <summary>
/// Optional OUTBOUND alert delivery to a generic webhook (Slack/Teams/custom). MUST
/// gate on <see cref="IEgressGuard"/> so it fails closed under air-gap — mirroring the
/// billing connectors + live catalog sync. In solo/ephemeral this throws before any
/// HTTP, so alerts stay in the in-app feed only. HttpClient is injected (testable).
/// </summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly Uri _webhook;
    private readonly IEgressGuard _egress;

    public WebhookNotifier(HttpClient http, Uri webhook, IEgressGuard egress)
        => (_http, _webhook, _egress) = (http, webhook, egress);

    public async Task NotifyAsync(Alert alert, CancellationToken ct = default)
    {
        _egress.AssertEgressAllowed(_webhook.Host, "budget-alert-webhook");   // fail closed under air-gap
        using var resp = await _http.PostAsJsonAsync(_webhook, alert, ct);
        resp.EnsureSuccessStatusCode();
    }
}
