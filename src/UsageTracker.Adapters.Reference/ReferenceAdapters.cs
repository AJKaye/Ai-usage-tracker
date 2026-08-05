using System.Runtime.CompilerServices;
using UsageTracker.Contracts;

namespace UsageTracker.Adapters.Reference;

/// <summary>
/// UiPath adapter (ARCHITECTURE.md §7 — a token-LESS coarse surface). UiPath meters
/// embedded LLM work as abstract "AI units" (a GenAI activity = 1 unit, 2 with
/// Context Grounding); there is no per-token signal. Emits credit-granularity spans
/// priced via the per-unit path. In a real build this pulls Automation Cloud
/// Insights / licensing; here it yields a representative event so the SDK surface is
/// exercised without a network dependency.
/// </summary>
public sealed class UiPathUsageAdapter : IUsageAdapter
{
    public string SourceId => "uipath";

    public async IAsyncEnumerable<Span> PullAsync(
        string tenantId, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new Span
        {
            TenantId = tenantId,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Tool,
            Provider = "uipath",
            Granularity = Granularity.Credit,     // no tokens — abstract AI units
            UnitsConsumed = 2,                     // GenAI activity w/ Context Grounding
            UnitType = "ai_unit",
            StartTime = since,
            Metadata = new Dictionary<string, string> { ["surface"] = "uipath", ["process"] = "invoice-triage" },
        };
    }
}

/// <summary>
/// GitHub Copilot adapter (ARCHITECTURE.md §7 — NO token signal anywhere). The
/// billable lever is premium requests (per-prompt × per-model multiplier, $0.04/req
/// overage) plus seats. Emits request- and seat-granularity spans; the cost engine
/// prices them via the per-request / per-seat unit rates.
/// NOTE: the adapter must set UnitsConsumed = billable OVERAGE (total − included
/// allotment); the engine prices what it's given.
/// </summary>
public sealed class GitHubCopilotUsageAdapter : IUsageAdapter
{
    public string SourceId => "github-copilot";

    public async IAsyncEnumerable<Span> PullAsync(
        string tenantId, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        // Premium-request overage (beyond the included allotment).
        yield return new Span
        {
            TenantId = tenantId,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Tool,
            Provider = "github",
            Granularity = Granularity.Request,
            UnitsConsumed = 5,                    // billable overage requests
            UnitType = "premium_request",
            StartTime = since,
            Metadata = new Dictionary<string, string> { ["surface"] = "copilot" },
        };

        // Seat licences (one span per billing period; the summary layer must not
        // double-count daily).
        yield return new Span
        {
            TenantId = tenantId,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Tool,
            Provider = "github",
            Granularity = Granularity.Seat,
            UnitsConsumed = 3,                    // active seats this period
            UnitType = "copilot_seat",
            StartTime = since,
            Metadata = new Dictionary<string, string> { ["surface"] = "copilot", ["period"] = "monthly" },
        };
    }
}

/// <summary>
/// Claude Code adapter (ARCHITECTURE.md §7 — real token counts). Server-side the
/// Anthropic Admin API's usage_report/messages gives authoritative multi-bucket
/// tokens; client-side OTel gives the same shape. Emits token-granularity spans, so
/// the full subset-vs-additive + cost path applies (Anthropic = additive).
/// </summary>
public sealed class ClaudeCodeUsageAdapter : IUsageAdapter
{
    public string SourceId => "claude-code";

    public async IAsyncEnumerable<Span> PullAsync(
        string tenantId, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new Span
        {
            TenantId = tenantId,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Agent,
            Provider = "anthropic",
            ResponseModel = "claude-opus-5",
            Granularity = Granularity.Token,
            RawUsage = new TokenUsage
            {
                InputTokens = 300,                // Anthropic additive: uncached remainder
                CacheReadInputTokens = 1500,
                CacheCreationInputTokens = 200,
                OutputTokens = 800,
            },
            StartTime = since,
            Metadata = new Dictionary<string, string> { ["surface"] = "claude-code", ["query_source"] = "cli" },
        };
    }
}
