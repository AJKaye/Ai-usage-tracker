using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using UsageTracker.Contracts;
using UsageTracker.Mcp;
using UsageTracker.Storage.InMemory;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 9 / Increment 1 — the MCP server face (ARCHITECTURE.md §7.1). JSON-RPC 2.0
/// handshake + tools + resources over the shared IEventStore query surface.
/// </summary>
public class McpServerTests
{
    private static async Task<McpServer> ServerWith(params Span[] spans)
    {
        var store = new InMemoryEventStore();
        foreach (var s in spans) await store.AppendAsync(s);
        return new McpServer(new McpUsageProvider(store));
    }

    private static Span Costed(string spanId, string provider, decimal cost, long input, long output) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = spanId, Kind = SpanKind.Llm, Provider = provider,
        Usage = new NormalizedUsage
        {
            InputTokens = input, UncachedInputTokens = input, CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0, OutputTokens = output, ReasoningOutputTokens = 0,
        },
        StartTime = DateTimeOffset.UnixEpoch,
        EstimatedCost = new CostBreakdown { TotalCost = cost, Currency = "USD", Components = Array.Empty<CostComponent>(), Tier = "PriceMap" },
    };

    private static JsonElement Req(string json) => JsonDocument.Parse(json).RootElement;

    // Serialize the response object back to JSON so we can assert on the wire shape.
    private static JsonElement Wire(object? response) =>
        JsonDocument.Parse(JsonSerializer.Serialize(response)).RootElement;

    [Fact]
    public async Task Initialize_returns_protocol_and_server_info_echoing_id()
    {
        var mcp = await ServerWith();
        var resp = Wire(await mcp.HandleAsync(Req("""{"jsonrpc":"2.0","id":1,"method":"initialize"}"""), "t"));

        Assert.Equal("2.0", resp.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, resp.GetProperty("id").GetInt64());
        Assert.Equal(McpServer.ProtocolVersion, resp.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("ai-usage-tracker", resp.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tools_list_advertises_usage_tools_with_output_schema()
    {
        var mcp = await ServerWith();
        var resp = Wire(await mcp.HandleAsync(Req("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""), "t"));

        var tools = resp.GetProperty("result").GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("usage_summary", names);
        Assert.Contains("cost_by_provider", names);
        Assert.Contains("recent_spans", names);
        // The Phase-9 deliverable: tools carry an outputSchema.
        var summaryTool = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "usage_summary");
        Assert.True(summaryTool.TryGetProperty("outputSchema", out _));
        Assert.True(summaryTool.TryGetProperty("inputSchema", out _));
    }

    [Fact]
    public async Task Tools_call_usage_summary_returns_structured_totals()
    {
        var mcp = await ServerWith(
            Costed("s1", "anthropic", 0.0175m, 1000, 500),
            Costed("s2", "openai", 0.0080m, 800, 200));
        var resp = Wire(await mcp.HandleAsync(
            Req("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"usage_summary","arguments":{}}}"""), "t"));

        var structured = resp.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(2, structured.GetProperty("spanCount").GetInt32());
        Assert.Equal(0.0255m, structured.GetProperty("totalEstimatedCost").GetDecimal());   // 0.0175 + 0.0080
        Assert.Equal(2500, structured.GetProperty("totalInputTokens").GetInt64() + structured.GetProperty("totalOutputTokens").GetInt64());
        // human-readable content[] present too
        Assert.False(resp.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("text", resp.GetProperty("result").GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Tools_call_is_tenant_scoped()
    {
        var mcp = await ServerWith(Costed("s1", "anthropic", 5.00m, 100, 100));   // tenant "t"
        var resp = Wire(await mcp.HandleAsync(
            Req("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"usage_summary"}}"""), "other-tenant"));
        Assert.Equal(0, resp.GetProperty("result").GetProperty("structuredContent").GetProperty("spanCount").GetInt32());
    }

    [Fact]
    public async Task Resources_read_returns_recent_spans_json()
    {
        var mcp = await ServerWith(Costed("s1", "anthropic", 0.01m, 100, 50));
        var resp = Wire(await mcp.HandleAsync(
            Req("""{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"usage://recent-spans"}}"""), "t"));
        var contents = resp.GetProperty("result").GetProperty("contents")[0];
        Assert.Equal("application/json", contents.GetProperty("mimeType").GetString());
        Assert.Contains("s1", contents.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Unknown_method_is_jsonrpc_method_not_found()
    {
        var mcp = await ServerWith();
        var resp = Wire(await mcp.HandleAsync(Req("""{"jsonrpc":"2.0","id":6,"method":"does/not/exist"}"""), "t"));
        Assert.Equal(-32601, resp.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Unknown_tool_is_invalid_params()
    {
        var mcp = await ServerWith();
        var resp = Wire(await mcp.HandleAsync(
            Req("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"nope"}}"""), "t"));
        Assert.Equal(-32602, resp.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Initialized_notification_yields_no_response()
    {
        var mcp = await ServerWith();
        var resp = await mcp.HandleAsync(Req("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), "t");
        Assert.Null(resp);
    }
}

/// <summary>Phase 9 — MCP over the real HTTP endpoint.</summary>
public class McpEndToEndTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public McpEndToEndTests(EphemeralApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Mcp_endpoint_lists_tools_and_answers_usage_summary()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "mcp-e2e");

        // Ingest one event.
        await client.PostAsync("/v1/ingest", new StringContent(
            """{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5","gen_ai.usage.input_tokens":1000,"gen_ai.usage.output_tokens":500,"span_id":"mcp-1","kind":"llm"}""",
            Encoding.UTF8, "application/json"));

        // tools/list over the wire.
        var list = await client.PostAsync("/mcp", new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var d = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
            Assert.True(d.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() >= 3);

        // tools/call usage_summary → the ingested event's cost.
        var call = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "usage_summary", arguments = new { } },
        });
        using var doc = JsonDocument.Parse(await call.Content.ReadAsStringAsync());
        var structured = doc.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(1, structured.GetProperty("spanCount").GetInt32());
        Assert.Equal(0.0175m, structured.GetProperty("totalEstimatedCost").GetDecimal());
    }
}
