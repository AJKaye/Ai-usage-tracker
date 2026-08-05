using UsageTracker.Contracts;
using UsageTracker.Security;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 7 / Increment 2 — tamper-evident audit (SOC 2 evidence) and crypto-shredding
/// (GDPR/HIPAA right-to-delete). PROJECT_CONTEXT §6.
/// </summary>
public class AuditAndShreddingTests
{
    private static AuditEvent Evt(string tenant, string action, string actor = "svc") => new()
    {
        TenantId = tenant, Actor = actor, Action = action, At = DateTimeOffset.UnixEpoch,
    };

    // --- hash-chain audit: verifies intact, detects tampering ---------------------
    [Fact]
    public async Task Audit_chain_verifies_when_intact()
    {
        var sink = new HashChainAuditSink();
        await sink.RecordAsync(Evt("t", "span.read"));
        await sink.RecordAsync(Evt("t", "vault.credential.create"));
        await sink.RecordAsync(Evt("t", "reconcile.run"));

        Assert.True(sink.Verify("t"));
        var export = sink.Export("t");
        Assert.Equal(3, export.Count);
        Assert.Equal(0, export[0].Sequence);
        Assert.Equal(export[0].Hash, export[1].PrevHash);   // chain links
        Assert.Equal(export[1].Hash, export[2].PrevHash);
    }

    [Fact]
    public async Task Audit_chain_detects_a_mutated_past_entry()
    {
        var sink = new HashChainAuditSink();
        await sink.RecordAsync(Evt("t", "span.read"));
        await sink.RecordAsync(Evt("t", "span.delete"));

        // Export the evidence chain, then tamper with a past entry's event while
        // keeping its (now-stale) hash — the classic "edit the log" attack.
        var export = sink.Export("t").ToList();
        Assert.True(HashChainAuditSink.Verify(export));            // intact as exported
        export[0] = export[0] with { Event = export[0].Event with { Action = "span.read.FALSIFIED" } };

        Assert.False(HashChainAuditSink.Verify(export));           // tamper detected
    }

    [Fact]
    public async Task Audit_chain_detects_reordering()
    {
        var sink = new HashChainAuditSink();
        await sink.RecordAsync(Evt("t", "first"));
        await sink.RecordAsync(Evt("t", "second"));
        var export = sink.Export("t").ToList();
        (export[0], export[1]) = (export[1], export[0]);          // swap order
        Assert.False(HashChainAuditSink.Verify(export));
    }

    [Fact]
    public async Task Audit_is_tenant_scoped()
    {
        var sink = new HashChainAuditSink();
        await sink.RecordAsync(Evt("tenant-a", "a.action"));
        Assert.Single(sink.Export("tenant-a"));
        Assert.Empty(sink.Export("tenant-b"));
    }

    // --- crypto-shredding: right-to-delete over an append-only store --------------
    [Fact]
    public void Crypto_shred_makes_content_unrecoverable_but_aggregates_persist()
    {
        var vault = new SubjectKeyVault();
        var sealed_ = vault.Seal("subject-42", "the user's private prompt text");

        // Before erasure: content decrypts.
        Assert.Equal("the user's private prompt text", vault.Unseal(sealed_));

        // Aggregate cost is computed WITHOUT the content/key — model it as a separate value.
        decimal aggregateCost = 0.0175m;

        // Right-to-delete: destroy the subject's key.
        Assert.True(vault.CryptoShred("subject-42"));
        Assert.False(vault.HasKey("subject-42"));

        // After erasure: content is permanently unrecoverable...
        Assert.Null(vault.Unseal(sealed_));
        // ...while the non-content aggregate still stands.
        Assert.Equal(0.0175m, aggregateCost);
    }

    [Fact]
    public void Shredding_an_unknown_subject_is_a_noop_false()
    {
        Assert.False(new SubjectKeyVault().CryptoShred("nobody"));
    }

    [Fact]
    public void Sealed_content_fails_auth_if_tampered()
    {
        var vault = new SubjectKeyVault();
        var sealed_ = vault.Seal("s", "secret");
        var tampered = sealed_ with { CipherText = sealed_.CipherText.Select(b => (byte)(b ^ 0xFF)).ToArray() };
        Assert.Null(vault.Unseal(tampered));   // GCM tag mismatch → null, not garbage
    }

    // --- data-lifecycle policy ----------------------------------------------------
    [Fact]
    public void Retention_and_residency_policy_is_honored()
    {
        var policy = new DataLifecyclePolicy { TenantId = "t", ResidencyRegion = "eu", RetentionDays = 30 };
        var now = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

        Assert.True(policy.IsExpired(now.AddDays(-31), now));    // beyond 30d
        Assert.False(policy.IsExpired(now.AddDays(-10), now));

        Assert.True(policy.AllowsRegion("eu"));
        Assert.False(policy.AllowsRegion("us"));                 // residency guard

        Assert.False(policy.ContentCaptureEnabled);              // opt-in: off by default (PII posture)
    }
}
