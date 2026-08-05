using UsageTracker.Contracts;

namespace UsageTracker.FinOps;

/// <summary>Total estimated cost ÷ total tokens (input+output) — cost per 1 token.</summary>
public sealed class CostPerTokenMetric : IUnitMetric
{
    public string Name => "cost_per_token";
    public string Unit => "USD/token";

    public decimal Compute(IReadOnlyList<Span> spans, long denominator)
    {
        long tokens = spans.Sum(s => (s.Usage?.TotalTokens) ?? 0);
        if (tokens <= 0) return 0m;
        return TotalCost(spans) / tokens;
    }

    internal static decimal TotalCost(IReadOnlyList<Span> spans) =>
        spans.Sum(s => s.EstimatedCost?.TotalCost ?? 0m);
}

/// <summary>Total estimated cost ÷ number of inferences (LLM-kind spans) — cost per call.</summary>
public sealed class CostPerInferenceMetric : IUnitMetric
{
    public string Name => "cost_per_inference";
    public string Unit => "USD/inference";

    public decimal Compute(IReadOnlyList<Span> spans, long denominator)
    {
        long calls = spans.Count(s => s.Kind == SpanKind.Llm);
        if (calls <= 0) return 0m;
        return CostPerTokenMetric.TotalCost(spans) / calls;
    }
}

/// <summary>
/// Total estimated cost ÷ a caller-supplied outcome count — the agentic-era metric
/// (cost per assist / case deflected / agent action; ARCHITECTURE.md §6.3). The
/// denominator is business-defined and passed in, so the engine stays generic.
/// </summary>
public sealed class CostPerOutcomeMetric : IUnitMetric
{
    public string Name => "cost_per_outcome";
    public string Unit => "USD/outcome";

    public decimal Compute(IReadOnlyList<Span> spans, long denominator)
    {
        if (denominator <= 0) return 0m;
        return CostPerTokenMetric.TotalCost(spans) / denominator;
    }
}

/// <summary>
/// Operational-efficiency roll-up derived purely from span telemetry
/// (ARCHITECTURE.md §6.3): latency, TTFT, cache-hit rate, error rate, throughput.
/// Cheap and near-universal — no eval engine required.
/// </summary>
public sealed record EfficiencySummary
{
    public required int SpanCount { get; init; }
    public required double AvgDurationMs { get; init; }
    public required double? AvgTimeToFirstTokenMs { get; init; }
    public required double CacheHitRate { get; init; }     // fraction of input tokens served from cache
    public required double ErrorRate { get; init; }        // fraction of spans with a non-ok status
    public required long TotalTokens { get; init; }
}

public static class EfficiencyCalculator
{
    public static EfficiencySummary Compute(IReadOnlyList<Span> spans)
    {
        if (spans.Count == 0)
            return new EfficiencySummary
            {
                SpanCount = 0, AvgDurationMs = 0, AvgTimeToFirstTokenMs = null,
                CacheHitRate = 0, ErrorRate = 0, TotalTokens = 0,
            };

        double durSum = 0; int durN = 0;
        double ttftSum = 0; int ttftN = 0;
        long cacheRead = 0, totalInput = 0, totalTokens = 0;
        int errors = 0;

        foreach (var s in spans)
        {
            if (s.EndTime is { } end)
            {
                durSum += (end - s.StartTime).TotalMilliseconds;
                durN++;
            }
            if (s.TimeToFirstTokenMs is { } ttft) { ttftSum += ttft; ttftN++; }

            if (s.Usage is { } u)
            {
                cacheRead += u.CacheReadInputTokens;
                totalInput += u.InputTokens;
                totalTokens += u.TotalTokens;
            }
            if (s.Status is { } st && !string.Equals(st, "ok", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(st, "success", StringComparison.OrdinalIgnoreCase))
                errors++;
        }

        return new EfficiencySummary
        {
            SpanCount = spans.Count,
            AvgDurationMs = durN > 0 ? durSum / durN : 0,
            AvgTimeToFirstTokenMs = ttftN > 0 ? ttftSum / ttftN : null,
            CacheHitRate = totalInput > 0 ? (double)cacheRead / totalInput : 0,
            ErrorRate = (double)errors / spans.Count,
            TotalTokens = totalTokens,
        };
    }
}
