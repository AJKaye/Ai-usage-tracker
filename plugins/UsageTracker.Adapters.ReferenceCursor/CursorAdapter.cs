using System.Runtime.CompilerServices;
using UsageTracker.Contracts;

// Declares the contract version this plugin was built against. The host's
// PluginLoader reads this and refuses the plugin on a MAJOR mismatch.
[assembly: UsageTrackerPlugin(contractMajor: 0, contractMinor: 1)]

namespace UsageTracker.Adapters.ReferenceCursor;

/// <summary>
/// Reference plugin entry point. A real plugin's <see cref="CreateAdapters"/>
/// would wire config/secrets; this returns the sample adapter.
/// </summary>
public sealed class ReferenceCursorPlugin : IUsageTrackerPlugin
{
    public string Name => "reference-cursor";
    public IEnumerable<IUsageAdapter> CreateAdapters() => new IUsageAdapter[] { new CursorUsageAdapter() };
}

/// <summary>
/// Pull-adapter shaped after the Cursor Admin API (ARCHITECTURE.md §7 — a
/// token-level closed surface). In a real build this calls
/// POST /teams/filtered-usage-events; here it yields a representative span so the
/// plugin seam is exercised end-to-end without a network dependency.
/// </summary>
public sealed class CursorUsageAdapter : IUsageAdapter
{
    public string SourceId => "cursor";

    public async IAsyncEnumerable<Span> PullAsync(
        string tenantId, DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield(); // stand-in for the async HTTP pull

        yield return new Span
        {
            TenantId = tenantId,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Llm,
            Provider = "anthropic",              // Cursor reports the underlying model
            ResponseModel = "claude-sonnet-5",
            Granularity = Granularity.Token,
            RawUsage = new TokenUsage
            {
                InputTokens = 1200,              // Cursor gives real token counts
                CacheReadInputTokens = 800,
                OutputTokens = 350,
            },
            StartTime = since,
            UserId = "cursor-user@example.com",
            Metadata = new Dictionary<string, string> { ["surface"] = "cursor", ["kind"] = "agent" },
        };
    }
}
