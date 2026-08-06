using System.Text.Json;
using System.Text.Json.Serialization;
using UsageTracker.Contracts;

namespace UsageTracker.Portability;

/// <summary>
/// A portable export bundle for one tenant's data (Phase 10: backup/restore +
/// data-export/portability, and the substrate for a solo→distributed migration).
/// Versioned so a future schema change is detectable on import. Content
/// (prompts/responses) is never here — the canonical Span excludes it, so an export
/// carries usage/cost/attribution only (PII posture holds through backup).
/// </summary>
public sealed record ExportBundle
{
    public const int CurrentFormat = 1;

    [JsonPropertyName("format")] public int Format { get; init; } = CurrentFormat;
    [JsonPropertyName("tenant")] public required string Tenant { get; init; }
    [JsonPropertyName("exportedAt")] public required DateTimeOffset ExportedAt { get; init; }
    [JsonPropertyName("spanCount")] public int SpanCount => Spans.Count;
    [JsonPropertyName("spans")] public required IReadOnlyList<Span> Spans { get; init; }
}

/// <summary>Outcome of an import/restore — how many spans were written vs skipped.</summary>
public sealed record ImportResult
{
    public required int Imported { get; init; }
    public required int Skipped { get; init; }
    public required string Tenant { get; init; }
}

/// <summary>
/// Exports and imports a tenant's data over ANY <see cref="IEventStore"/>. Because it
/// speaks only the contract, an export from the embedded SQLite store imports into a
/// server store unchanged — that IS the documented solo→distributed migration path
/// (no bespoke ETL). Import is idempotent: the store keys on (tenant, span), so
/// re-importing the same bundle is a safe no-op. All operations are tenant-scoped.
/// </summary>
public sealed class DataPortabilityService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IEventStore _store;
    private readonly TimeProvider _clock;

    public DataPortabilityService(IEventStore store, TimeProvider? clock = null)
        => (_store, _clock) = (store, clock ?? TimeProvider.System);

    /// <summary>Export all of a tenant's spans into a portable bundle.</summary>
    public async Task<ExportBundle> ExportAsync(string tenantId, CancellationToken ct = default)
    {
        var spans = await _store.QueryAsync(new SpanQuery { TenantId = tenantId, Limit = int.MaxValue }, ct);
        return new ExportBundle
        {
            Tenant = tenantId,
            ExportedAt = _clock.GetUtcNow(),
            Spans = spans,
        };
    }

    /// <summary>Serialize a bundle to JSON (backup file / migration payload).</summary>
    public async Task<string> ExportJsonAsync(string tenantId, CancellationToken ct = default)
        => JsonSerializer.Serialize(await ExportAsync(tenantId, ct), Json);

    /// <summary>
    /// Import a bundle into the store under <paramref name="tenantId"/>. Spans are
    /// re-tenanted to the target tenant (so a bundle can be restored into a fresh
    /// tenant), and idempotent by (tenant, span). Rejects an unknown bundle format.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string tenantId, ExportBundle bundle, CancellationToken ct = default)
    {
        if (bundle.Format != ExportBundle.CurrentFormat)
            throw new NotSupportedException($"unsupported export format {bundle.Format} (this build imports format {ExportBundle.CurrentFormat}).");

        int imported = 0, skipped = 0;
        foreach (var span in bundle.Spans)
        {
            if (string.IsNullOrEmpty(span.SpanId)) { skipped++; continue; }
            // Re-tenant to the target so restore-into-a-new-tenant is well-defined.
            await _store.AppendAsync(span with { TenantId = tenantId }, ct);
            imported++;
        }
        return new ImportResult { Imported = imported, Skipped = skipped, Tenant = tenantId };
    }

    /// <summary>Import from a JSON bundle string.</summary>
    public Task<ImportResult> ImportJsonAsync(string tenantId, string json, CancellationToken ct = default)
    {
        var bundle = JsonSerializer.Deserialize<ExportBundle>(json, Json)
            ?? throw new ArgumentException("empty or invalid export bundle JSON.");
        return ImportAsync(tenantId, bundle, ct);
    }
}
