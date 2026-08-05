using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Mcp;

/// <summary>
/// A minimal MCP (Model Context Protocol) server over JSON-RPC 2.0
/// (modelcontextprotocol.io; ARCHITECTURE.md §7.1). Implements the handshake +
/// tools + resources methods so any MCP client can query its own AI spend live:
///   • initialize                → protocol/capabilities handshake
///   • tools/list                → the usage tools (with input + output schemas)
///   • tools/call                → run a tool (tenant-scoped) → structuredContent
///   • resources/list / read     → recent spans as a resource
/// Transport-agnostic and pure (in → out), so it's fully unit-testable; the HTTP
/// host just pipes request/response JSON. Tenant is resolved by the host and passed
/// in — the server never trusts a client-supplied tenant.
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";
    private readonly IMcpUsageProvider _provider;

    public McpServer(IMcpUsageProvider provider) => _provider = provider;

    /// <summary>Handle one JSON-RPC request object; returns the response object (or null for a notification).</summary>
    public async Task<object?> HandleAsync(JsonElement req, string tenantId, CancellationToken ct = default)
    {
        // id may be absent (notification), a number, or a string — echo it back verbatim.
        object? id = req.TryGetProperty("id", out var idEl)
            ? idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.GetInt64(),
                JsonValueKind.String => idEl.GetString(),
                _ => null,
            }
            : null;

        string method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        var @params = req.TryGetProperty("params", out var p) ? p : default;

        try
        {
            return method switch
            {
                "initialize" => Ok(id, Initialize()),
                "tools/list" => Ok(id, new { tools = McpTools.List }),
                "tools/call" => Ok(id, await CallToolAsync(@params, tenantId, ct)),
                "resources/list" => Ok(id, new { resources = McpResources.List }),
                "resources/read" => Ok(id, await ReadResourceAsync(@params, tenantId, ct)),
                "ping" => Ok(id, new { }),
                "notifications/initialized" => null,   // notification — no response
                _ => Err(id, -32601, $"method not found: {method}"),
            };
        }
        catch (McpToolException ex)
        {
            return Err(id, -32602, ex.Message);
        }
    }

    private object Initialize() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new { tools = new { }, resources = new { } },
        serverInfo = new { name = "ai-usage-tracker", version = "0.1.0" },
    };

    private async Task<object> CallToolAsync(JsonElement @params, string tenantId, CancellationToken ct)
    {
        string name = @params.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var args = @params.TryGetProperty("arguments", out var a) ? a : default;

        switch (name)
        {
            case "usage_summary":
            {
                var s = await _provider.QuerySummaryAsync(new SpanQuery { TenantId = tenantId, Limit = int.MaxValue }, ct);
                var structured = new
                {
                    spanCount = s.SpanCount,
                    totalInputTokens = s.TotalInputTokens,
                    totalOutputTokens = s.TotalOutputTokens,
                    totalEstimatedCost = s.TotalEstimatedCost,
                    currency = s.Currency,
                };
                return ToolResult($"{s.SpanCount} events, {s.TotalEstimatedCost} {s.Currency} estimated.", structured);
            }
            case "cost_by_provider":
            {
                var s = await _provider.QuerySummaryAsync(new SpanQuery { TenantId = tenantId, Limit = int.MaxValue }, ct);
                return ToolResult("cost grouped by provider", new { costByProvider = s.CostByProvider, currency = s.Currency });
            }
            case "recent_spans":
            {
                int limit = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
                    ? l.GetInt32() : 20;
                var spans = await _provider.RecentSpansAsync(tenantId, limit, ct);
                var rows = spans.Select(sp => new { sp.SpanId, sp.Provider, model = sp.ResponseModel ?? sp.RequestModel, cost = sp.EstimatedCost?.TotalCost ?? 0m });
                return ToolResult($"{spans.Count} recent spans", new { spans = rows });
            }
            default:
                throw new McpToolException($"unknown tool: {name}");
        }
    }

    private async Task<object> ReadResourceAsync(JsonElement @params, string tenantId, CancellationToken ct)
    {
        string uri = @params.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
        if (uri != "usage://recent-spans") throw new McpToolException($"unknown resource: {uri}");
        var spans = await _provider.RecentSpansAsync(tenantId, 50, ct);
        var text = JsonSerializer.Serialize(spans.Select(sp => new { sp.SpanId, sp.Provider, model = sp.ResponseModel, cost = sp.EstimatedCost?.TotalCost ?? 0m }));
        return new { contents = new[] { new { uri, mimeType = "application/json", text } } };
    }

    // MCP tools/call result: human-readable content[] + structuredContent (for outputSchema consumers).
    private static object ToolResult(string summary, object structured) => new
    {
        content = new[] { new { type = "text", text = summary } },
        structuredContent = structured,
        isError = false,
    };

    private static object Ok(object? id, object result) => new { jsonrpc = "2.0", id, result };
    private static object Err(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
}

/// <summary>Bad tool/resource params → JSON-RPC invalid-params.</summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}
