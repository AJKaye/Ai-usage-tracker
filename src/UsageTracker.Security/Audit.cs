using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Security;

/// <summary>One tamper-evident audit record: the event + its position and hash-chain link.</summary>
public sealed record AuditRecord
{
    public required long Sequence { get; init; }
    public required AuditEvent Event { get; init; }
    public required string PrevHash { get; init; }
    public required string Hash { get; init; }
}

/// <summary>
/// Immutable, tamper-evident audit sink (PROJECT_CONTEXT §6; SOC 2 evidence). Each
/// record's hash is <c>SHA-256(prevHash ‖ canonical(event) ‖ sequence)</c>, so the
/// log is a hash chain: mutating, deleting, or reordering ANY past entry breaks every
/// subsequent hash and <see cref="Verify"/> detects it. Append-only and tenant-scoped;
/// exportable for evidence. The embedded (solo) impl of <see cref="IAuditSink"/>; a
/// WORM/object-lock store satisfies the same contract in the scale tier.
/// </summary>
public sealed class HashChainAuditSink : IAuditSink
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    // tenant -> ordered chain
    private readonly ConcurrentDictionary<string, List<AuditRecord>> _byTenant = new();
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    public Task RecordAsync(AuditEvent evt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var chain = _byTenant.GetOrAdd(evt.TenantId, _ => new List<AuditRecord>());
            long seq = chain.Count;
            string prev = chain.Count == 0 ? Genesis : chain[^1].Hash;
            string hash = ComputeHash(prev, evt, seq);
            chain.Add(new AuditRecord { Sequence = seq, Event = evt, PrevHash = prev, Hash = hash });
        }
        return Task.CompletedTask;
    }

    /// <summary>The tenant's append-only audit chain (SOC 2 evidence export).</summary>
    public IReadOnlyList<AuditRecord> Export(string tenantId) =>
        _byTenant.TryGetValue(tenantId, out var chain) ? chain.ToList() : Array.Empty<AuditRecord>();

    /// <summary>
    /// Re-derive every hash from genesis; returns false if any link is broken (a record
    /// was mutated, inserted, deleted, or reordered). This is the tamper-evidence check.
    /// </summary>
    public bool Verify(string tenantId)
    {
        if (!_byTenant.TryGetValue(tenantId, out var chain)) return true;   // empty = trivially intact
        return Verify(chain);
    }

    /// <summary>
    /// Verify an arbitrary chain (e.g. one exported and transported for evidence).
    /// Re-derives every hash from genesis; false if any record was mutated, inserted,
    /// deleted, or reordered.
    /// </summary>
    public static bool Verify(IReadOnlyList<AuditRecord> chain)
    {
        string prev = Genesis;
        for (int i = 0; i < chain.Count; i++)
        {
            var rec = chain[i];
            if (rec.Sequence != i || rec.PrevHash != prev) return false;
            if (ComputeHash(prev, rec.Event, rec.Sequence) != rec.Hash) return false;
            prev = rec.Hash;
        }
        return true;
    }

    private static string ComputeHash(string prevHash, AuditEvent evt, long seq)
    {
        var payload = $"{prevHash}|{seq}|{JsonSerializer.Serialize(evt, Canonical)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
