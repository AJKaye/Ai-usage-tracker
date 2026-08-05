namespace UsageTracker.Contracts;

/// <summary>
/// The MCP server face (ARCHITECTURE.md §7.1): exposes usage/cost queries as MCP
/// tools/resources so an agent can read its own spend live. Kept a contract so
/// the MCP transport is one adapter over the same query surface the REST API uses
/// — not a parallel data path.
/// </summary>
public interface IMcpUsageProvider
{
    /// <summary>Answer a bounded usage/cost question for a tenant (maps to an MCP tool call).</summary>
    Task<UsageSummary> QuerySummaryAsync(SpanQuery query, CancellationToken ct = default);

    /// <summary>Recent spans for a tenant (maps to an MCP resource).</summary>
    Task<IReadOnlyList<Span>> RecentSpansAsync(string tenantId, int limit, CancellationToken ct = default);
}
