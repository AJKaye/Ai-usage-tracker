using UsageTracker.Contracts;

namespace UsageTracker.FinOps;

/// <summary>
/// Tag-free / dimension-based cost allocation (ARCHITECTURE.md §6.2 — the frontier).
/// Attributes 100% of a span set's estimated spend into buckets keyed by a captured
/// span dimension (team, user, session, model, provider, environment, or any
/// <see cref="Span.Metadata"/> key such as "feature"/"agent"/"mcp.session") WITHOUT
/// requiring upstream tags. A span that lacks the chosen dimension lands in an
/// explicit "(unattributed)" bucket, so the buckets always sum to 100% of spend —
/// showback-first, no cost silently lost.
/// </summary>
public sealed class DimensionAllocationStrategy : IAllocationStrategy
{
    /// <summary>Well-known dimension used when none is caught (ARCHITECTURE.md §6.2).</summary>
    public const string Unattributed = "(unattributed)";

    public string Name => $"dimension:{Dimension}";
    public string Dimension { get; }

    public DimensionAllocationStrategy(string dimension) => Dimension = dimension;

    public IReadOnlyList<AllocationBucket> Allocate(IReadOnlyList<Span> spans)
    {
        var byKey = new Dictionary<string, (decimal cost, int count)>(StringComparer.OrdinalIgnoreCase);
        string currency = "USD";

        foreach (var span in spans)
        {
            var cost = span.EstimatedCost;
            if (cost is not null) currency = cost.Currency;
            var amount = cost?.TotalCost ?? 0m;

            var key = KeyFor(span, Dimension) ?? Unattributed;
            var prev = byKey.GetValueOrDefault(key);
            byKey[key] = (prev.cost + amount, prev.count + 1);
        }

        // Descending by cost so the biggest consumers surface first.
        return byKey
            .Select(kv => new AllocationBucket(kv.Key, kv.Value.cost, currency, kv.Value.count))
            .OrderByDescending(b => b.Cost)
            .ThenBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolve a dimension value from captured span fields — no tags needed.</summary>
    internal static string? KeyFor(Span span, string dimension) => dimension.ToLowerInvariant() switch
    {
        "team" => span.TeamId,
        "user" => span.UserId,
        "session" => span.SessionId,
        "model" => span.ResponseModel ?? span.RequestModel,
        "provider" => span.Provider,
        "environment" => span.Environment,
        "kind" => span.Kind.ToString(),
        // any other dimension is looked up in captured metadata (feature, agent, mcp.session, …)
        _ => span.Metadata is { } m && m.TryGetValue(dimension, out var v) ? v : null,
    };
}
