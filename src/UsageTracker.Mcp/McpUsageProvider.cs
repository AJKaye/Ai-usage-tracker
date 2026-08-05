using UsageTracker.Contracts;

namespace UsageTracker.Mcp;

/// <summary>
/// The MCP server face (ARCHITECTURE.md §7.1). A THIN adapter over the same
/// <see cref="IEventStore"/> query surface the REST API uses — MCP is one transport
/// over the shared data path, never a parallel one. Lets an agent read its own spend
/// live via MCP tools/resources. Tenant scoping flows through <see cref="SpanQuery"/>.
/// </summary>
public sealed class McpUsageProvider : IMcpUsageProvider
{
    private readonly IEventStore _store;
    public McpUsageProvider(IEventStore store) => _store = store;

    public Task<UsageSummary> QuerySummaryAsync(SpanQuery query, CancellationToken ct = default)
        => _store.SummarizeAsync(query, ct);

    public Task<IReadOnlyList<Span>> RecentSpansAsync(string tenantId, int limit, CancellationToken ct = default)
        => _store.QueryAsync(new SpanQuery { TenantId = tenantId, Limit = limit }, ct);
}
