namespace UsageTracker.Mcp;

/// <summary>The MCP tool descriptors advertised by tools/list. Each carries an
/// inputSchema and (per the Phase-9 deliverable) an outputSchema so agents can
/// consume the structuredContent typed.</summary>
public static class McpTools
{
    public static readonly object[] List =
    {
        new
        {
            name = "usage_summary",
            description = "Total AI spend + token usage for the calling tenant (estimated).",
            inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            outputSchema = new
            {
                type = "object",
                properties = new
                {
                    spanCount = new { type = "integer" },
                    totalInputTokens = new { type = "integer" },
                    totalOutputTokens = new { type = "integer" },
                    totalEstimatedCost = new { type = "number" },
                    currency = new { type = "string" },
                },
                required = new[] { "spanCount", "totalEstimatedCost", "currency" },
            },
        },
        new
        {
            name = "cost_by_provider",
            description = "Estimated cost grouped by provider for the calling tenant.",
            inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
            outputSchema = new
            {
                type = "object",
                properties = new { costByProvider = new { type = "object" }, currency = new { type = "string" } },
                required = new[] { "costByProvider" },
            },
        },
        new
        {
            name = "recent_spans",
            description = "The most recent AI usage spans for the calling tenant.",
            inputSchema = new
            {
                type = "object",
                properties = new { limit = new { type = "integer", description = "max spans to return (default 20)" } },
                additionalProperties = false,
            },
            outputSchema = new { type = "object", properties = new { spans = new { type = "array" } }, required = new[] { "spans" } },
        },
    };
}

/// <summary>MCP resource descriptors advertised by resources/list.</summary>
public static class McpResources
{
    public static readonly object[] List =
    {
        new
        {
            uri = "usage://recent-spans",
            name = "Recent usage spans",
            description = "The calling tenant's most recent AI usage spans as JSON.",
            mimeType = "application/json",
        },
    };
}
