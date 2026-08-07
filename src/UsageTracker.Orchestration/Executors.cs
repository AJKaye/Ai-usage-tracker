using System.Globalization;
using System.Text;
using UsageTracker.Contracts;

namespace UsageTracker.Orchestration;

/// <summary>
/// Shared helpers for building the canonical <see cref="Span"/> an executor returns. The
/// runner overlays identity (TenantId/TraceId/SpanId/ParentSpanId) + binding metadata
/// (agent/skill/workflow/node), so executors only fill the "body" (kind, model, usage). Keeping
/// this here means every executor emits spans the existing cost engine + normalizer understand.
/// </summary>
internal static class ExecutorSpan
{
    /// <summary>A span carrying pre-normalized token usage on a cataloged model → the PriceMap
    /// tier prices it. Used by the LLM/agent executors (real usage in 12b; synthetic in 12a).</summary>
    public static Span Llm(NodeExecutionContext ctx, SpanKind kind, string provider, string model,
        long inputTokens, long outputTokens)
    {
        return new Span
        {
            TenantId = ctx.TenantId,
            TraceId = ctx.TraceId,
            SpanId = ctx.SpanId,
            ParentSpanId = ctx.ParentSpanId,
            Kind = kind,
            Name = ctx.Node.SkillName ?? ctx.Node.Name,
            Provider = provider,
            ResponseModel = model,
            Granularity = Granularity.Token,
            Usage = new NormalizedUsage
            {
                InputTokens = inputTokens,
                UncachedInputTokens = inputTokens,
                CacheReadInputTokens = 0,
                CacheCreationInputTokens = 0,
                OutputTokens = outputTokens,
                ReasoningOutputTokens = 0,
            },
            StartTime = ctx.StartTime,
        };
    }

    /// <summary>A non-token span (transform/http) — no cost, classified Tool.</summary>
    public static Span Tool(NodeExecutionContext ctx, string? provider = null)
    {
        return new Span
        {
            TenantId = ctx.TenantId,
            TraceId = ctx.TraceId,
            SpanId = ctx.SpanId,
            ParentSpanId = ctx.ParentSpanId,
            Kind = SpanKind.Tool,
            Name = ctx.Node.SkillName ?? ctx.Node.Name,
            Provider = provider,
            Granularity = Granularity.Request,
            StartTime = ctx.StartTime,
        };
    }
}

/// <summary>
/// Pure, deterministic transform node — the "glue" between LLM/tool nodes. NEVER touches the
/// network, so it is identical in Phase 12a and 12b and always air-gap-safe. Supported ops
/// (Config["op"]): "template" (substitute {input} placeholders from inputs into Config["template"]),
/// "concat" (join all inputs with Config["separator"] ?? "\n"), "passthrough" (default — echo inputs).
/// </summary>
public sealed class TransformNodeExecutor : INodeExecutor
{
    public WorkflowNodeType Type => WorkflowNodeType.Transform;

    public Task<NodeExecutionResult> ExecuteAsync(
        NodeExecutionContext ctx, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var op = ctx.Node.Config.GetValueOrDefault("op", "passthrough");
        string result = op switch
        {
            "template" => ApplyTemplate(ctx.Node.Config.GetValueOrDefault("template", ""), inputs),
            "concat" => string.Join(ctx.Node.Config.GetValueOrDefault("separator", "\n"), inputs.Values),
            _ => inputs.Count == 1 ? inputs.Values.First() : string.Join("\n", inputs.Select(kv => $"{kv.Key}={kv.Value}")),
        };

        // Output port name: first declared output, else "output".
        var outName = ctx.Node.OutputSchema.FirstOrDefault()?.Name ?? "output";
        var outputs = new Dictionary<string, string> { [outName] = result };

        return Task.FromResult(new NodeExecutionResult
        {
            Outputs = outputs,
            Span = ExecutorSpan.Tool(ctx),
            OutputPreview = Preview(result),
        });
    }

    private static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> inputs)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in inputs) sb.Replace("{" + k + "}", v);
        return sb.ToString();
    }

    internal static string Preview(string s) => s.Length <= 120 ? s : s[..120] + "…";
}

/// <summary>
/// LLM prompt node — the flagship "agent performs a skill" step. Phase 12a STUB: emits a
/// deterministic costed span on a cataloged model (so the run shows real spend) without any
/// network call. Phase 12b replaces the body with a real provider call (egress-assert +
/// secret-by-name) — the interface is unchanged.
/// </summary>
public sealed class LlmNodeExecutor : INodeExecutor
{
    public WorkflowNodeType Type => WorkflowNodeType.Llm;

    public Task<NodeExecutionResult> ExecuteAsync(
        NodeExecutionContext ctx, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var model = ctx.Node.Config.GetValueOrDefault("model", "claude-opus-5");
        // Deterministic synthetic usage derived from the (resolved) prompt length so dry-run
        // and stub runs are reproducible. 12b will use the provider's real usage.* counts.
        var prompt = ExtractPrompt(ctx.Node, inputs);
        long inputTokens = Math.Max(1, prompt.Length / 4);
        long outputTokens = Math.Max(1, inputTokens / 2);

        var outName = ctx.Node.OutputSchema.FirstOrDefault()?.Name ?? "completion";
        var text = ctx.DryRun
            ? $"[dry-run completion for {ctx.Node.Name ?? ctx.Node.Id}]"
            : $"[stub completion for {ctx.Node.Name ?? ctx.Node.Id}]";

        return Task.FromResult(new NodeExecutionResult
        {
            Outputs = new Dictionary<string, string> { [outName] = text },
            Span = ExecutorSpan.Llm(ctx, SpanKind.Llm, "anthropic", model, inputTokens, outputTokens),
            OutputPreview = TransformNodeExecutor.Preview(text),
        });
    }

    internal static string ExtractPrompt(WorkflowNode node, IReadOnlyDictionary<string, string> inputs)
    {
        var template = node.Config.GetValueOrDefault("prompt", "");
        if (string.IsNullOrEmpty(template))
            return string.Join("\n", inputs.Values);
        var sb = new StringBuilder(template);
        foreach (var (k, v) in inputs) sb.Replace("{" + k + "}", v);
        return sb.ToString();
    }
}

/// <summary>
/// HTTP/tool node. Phase 12a STUB: emits a zero-cost Tool span and echoes a canned response
/// without any network call. Phase 12b adds a real allowlist-gated HTTP call.
/// </summary>
public sealed class HttpToolNodeExecutor : INodeExecutor
{
    public WorkflowNodeType Type => WorkflowNodeType.Http;

    public Task<NodeExecutionResult> ExecuteAsync(
        NodeExecutionContext ctx, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var url = ctx.Node.Config.GetValueOrDefault("url", "");
        var outName = ctx.Node.OutputSchema.FirstOrDefault()?.Name ?? "response";
        var body = ctx.DryRun ? $"[dry-run http {url}]" : $"[stub http response from {url}]";

        return Task.FromResult(new NodeExecutionResult
        {
            Outputs = new Dictionary<string, string> { [outName] = body },
            Span = ExecutorSpan.Tool(ctx, provider: "http"),
            OutputPreview = TransformNodeExecutor.Preview(body),
        });
    }
}

/// <summary>
/// Agentic sub-loop node — a multi-step agent (LLM + tools in a loop). Phase 12a STUB: emits a
/// single costed Agent span sized to a bounded iteration count. Phase 12b runs a real bounded
/// LLM+tool loop, each turn a child span.
/// </summary>
public sealed class AgentLoopNodeExecutor : INodeExecutor
{
    public WorkflowNodeType Type => WorkflowNodeType.Agent;

    public Task<NodeExecutionResult> ExecuteAsync(
        NodeExecutionContext ctx, IReadOnlyDictionary<string, string> inputs, CancellationToken ct = default)
    {
        var model = ctx.Node.Config.GetValueOrDefault("model", "claude-opus-5");
        int maxIter = int.TryParse(ctx.Node.Config.GetValueOrDefault("maxIterations", "3"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 3;

        var prompt = LlmNodeExecutor.ExtractPrompt(ctx.Node, inputs);
        long inputTokens = Math.Max(1, prompt.Length / 4) * maxIter;   // loop sums turns
        long outputTokens = Math.Max(1, inputTokens / 2);

        var outName = ctx.Node.OutputSchema.FirstOrDefault()?.Name ?? "result";
        var text = ctx.DryRun
            ? $"[dry-run agent loop ×{maxIter} for {ctx.Node.Name ?? ctx.Node.Id}]"
            : $"[stub agent loop ×{maxIter} for {ctx.Node.Name ?? ctx.Node.Id}]";

        return Task.FromResult(new NodeExecutionResult
        {
            Outputs = new Dictionary<string, string> { [outName] = text },
            Span = ExecutorSpan.Llm(ctx, SpanKind.Agent, "anthropic", model, inputTokens, outputTokens),
            OutputPreview = TransformNodeExecutor.Preview(text),
        });
    }
}
