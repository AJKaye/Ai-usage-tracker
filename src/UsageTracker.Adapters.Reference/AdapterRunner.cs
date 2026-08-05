using UsageTracker.Contracts;

namespace UsageTracker.Adapters.Reference;

/// <summary>A fixed poll interval for an adapter (implements the scheduling contract).</summary>
public sealed record AdapterSchedule(string SourceId, TimeSpan Interval) : IAdapterSchedule;

/// <summary>
/// Drives the pull-adapter archetype (ARCHITECTURE.md §7): on each tick it asks an
/// <see cref="IUsageAdapter"/> for usage since the last successful pull and hands
/// every canonical span to a sink (which normalizes + costs + stores). The runner
/// is deliberately transport-agnostic — it doesn't know about HTTP or the event
/// store — so it works identically for a plugin adapter or an in-tree one, and is
/// unit-testable with a fake sink.
///
/// Checkpointing: the last-pulled watermark is kept per (tenant, source) so a pull
/// resumes from where it left off. A pull that throws does NOT advance the
/// watermark, so the next tick retries the same window (at-least-once).
/// </summary>
public sealed class AdapterRunner
{
    private readonly IUsageAdapter _adapter;
    private readonly Func<Span, CancellationToken, Task> _sink;
    private readonly Dictionary<string, DateTimeOffset> _watermark = new();
    private readonly Action<string>? _log;

    public AdapterRunner(IUsageAdapter adapter, Func<Span, CancellationToken, Task> sink, Action<string>? log = null)
        => (_adapter, _sink, _log) = (adapter, sink, log);

    /// <summary>
    /// Run one pull cycle for a tenant. Returns the number of spans ingested. On
    /// adapter failure the watermark is preserved and the exception is swallowed
    /// (logged) so the scheduler keeps the source alive for the next tick.
    /// </summary>
    public async Task<int> RunOnceAsync(string tenantId, DateTimeOffset now, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{_adapter.SourceId}";
        var since = _watermark.GetValueOrDefault(key, now - TimeSpan.FromDays(1));
        int count = 0;
        try
        {
            await foreach (var span in _adapter.PullAsync(tenantId, since, ct))
            {
                await _sink(span, ct);
                count++;
            }
            _watermark[key] = now;   // advance only on a fully successful pull
        }
        catch (Exception ex)
        {
            _log?.Invoke($"adapter '{_adapter.SourceId}' pull failed for {tenantId} (watermark kept at {since:o}): {ex.Message}");
        }
        return count;
    }

    /// <summary>The watermark for a (tenant, source), for tests/observability.</summary>
    public DateTimeOffset? WatermarkFor(string tenantId) =>
        _watermark.TryGetValue($"{tenantId}:{_adapter.SourceId}", out var w) ? w : null;
}
