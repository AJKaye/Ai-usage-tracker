using System.Collections.Concurrent;
using UsageTracker.Contracts;

namespace UsageTracker.Storage.InMemory;

/// <summary>
/// In-memory <see cref="IEventStore"/> — satisfies the exact same contract as the
/// production ClickHouse store, so the seam is proven and the whole pipeline runs
/// with no Docker/DB (ARCHITECTURE.md §3.1; the plan's "≥2 implementations or a
/// fake" modularity rule). Tenant-scoped on every read — a query for tenant A can
/// never see tenant B's spans, mirroring the RLS the real store enforces.
/// Not durable; for dev/test and the walking-skeleton slice only.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    // tenantId -> (spanId -> span)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Span>> _byTenant = new();

    public Task AppendAsync(Span span, CancellationToken ct = default)
    {
        var tenant = _byTenant.GetOrAdd(span.TenantId, _ => new());
        tenant[span.SpanId] = span;   // idempotent by span id (dedup)
        return Task.CompletedTask;
    }

    public Task<Span?> GetAsync(string tenantId, string spanId, CancellationToken ct = default)
    {
        Span? result = null;
        if (_byTenant.TryGetValue(tenantId, out var tenant) && tenant.TryGetValue(spanId, out var span))
            result = span;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Span>> QueryAsync(SpanQuery q, CancellationToken ct = default)
    {
        IReadOnlyList<Span> result = Filter(q).OrderByDescending(s => s.StartTime).Take(q.Limit).ToList();
        return Task.FromResult(result);
    }

    public Task<UsageSummary> SummarizeAsync(SpanQuery q, CancellationToken ct = default)
    {
        var spans = Filter(q).ToList();
        long inTok = 0, outTok = 0;
        decimal total = 0m;
        string currency = "USD";
        var byProvider = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var byModel = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in spans)
        {
            if (s.Usage is { } u) { inTok += u.InputTokens; outTok += u.OutputTokens; }
            if (s.EstimatedCost is { } c)
            {
                total += c.TotalCost;
                currency = c.Currency;
                if (s.Provider is { } p) byProvider[p] = byProvider.GetValueOrDefault(p) + c.TotalCost;
                var model = s.ResponseModel ?? s.RequestModel;
                if (model is { } m) byModel[m] = byModel.GetValueOrDefault(m) + c.TotalCost;
            }
        }

        return Task.FromResult(new UsageSummary
        {
            SpanCount = spans.Count,
            TotalInputTokens = inTok,
            TotalOutputTokens = outTok,
            TotalEstimatedCost = total,
            Currency = currency,
            CostByProvider = byProvider,
            CostByModel = byModel,
        });
    }

    private IEnumerable<Span> Filter(SpanQuery q)
    {
        if (!_byTenant.TryGetValue(q.TenantId, out var tenant))
            return Enumerable.Empty<Span>();

        IEnumerable<Span> spans = tenant.Values;
        if (q.TraceId is { } t) spans = spans.Where(s => s.TraceId == t);
        if (q.Provider is { } p) spans = spans.Where(s => string.Equals(s.Provider, p, StringComparison.OrdinalIgnoreCase));
        if (q.Since is { } since) spans = spans.Where(s => s.StartTime >= since);
        return spans;
    }
}
