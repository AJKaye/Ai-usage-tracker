using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace UsageTracker.Security;

/// <summary>Sealed opt-in content: AES-256-GCM ciphertext + nonce + tag, bound to a subject's data key.</summary>
public sealed record SealedContent
{
    public required string SubjectId { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] CipherText { get; init; }
    public required byte[] Tag { get; init; }
}

/// <summary>
/// Per-subject data keys + crypto-shredding (PROJECT_CONTEXT §6; GDPR/HIPAA
/// right-to-delete over append-only stores). Opt-in content (prompts/responses) is
/// sealed under a per-subject AES-256-GCM data key. Right-to-delete is satisfied by
/// DESTROYING the subject's key — the ciphertext then becomes permanently
/// unrecoverable, while non-content aggregates (token counts, cost) that never
/// depended on the key persist unchanged. In production the data keys are wrapped by
/// a KEK in KMS/Vault (envelope encryption); this is the embedded/testable core.
/// </summary>
public sealed class SubjectKeyVault
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();

    /// <summary>Get-or-create the subject's 256-bit data key. (Deterministic keygen is injected in tests.)</summary>
    private byte[] KeyFor(string subjectId) =>
        _keys.GetOrAdd(subjectId, _ => RandomNumberGenerator.GetBytes(32));

    /// <summary>Register a specific key (test seam / KMS-provided key import).</summary>
    public void ImportKey(string subjectId, byte[] key256)
    {
        if (key256.Length != 32) throw new ArgumentException("data key must be 256-bit (32 bytes).", nameof(key256));
        _keys[subjectId] = key256;
    }

    public bool HasKey(string subjectId) => _keys.ContainsKey(subjectId);

    /// <summary>Seal opt-in content under the subject's data key.</summary>
    public SealedContent Seal(string subjectId, string plaintext)
    {
        var key = KeyFor(subjectId);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var gcm = new AesGcm(key, tag.Length);
        gcm.Encrypt(nonce, pt, ct, tag);
        return new SealedContent { SubjectId = subjectId, Nonce = nonce, CipherText = ct, Tag = tag };
    }

    /// <summary>
    /// Unseal — returns null if the subject's key has been crypto-shredded (right-to-delete
    /// honored) or if the ciphertext fails authentication (tamper).
    /// </summary>
    public string? Unseal(SealedContent sealed_)
    {
        if (!_keys.TryGetValue(sealed_.SubjectId, out var key)) return null;   // key destroyed → unrecoverable
        var pt = new byte[sealed_.CipherText.Length];
        try
        {
            using var gcm = new AesGcm(key, sealed_.Tag.Length);
            gcm.Decrypt(sealed_.Nonce, sealed_.CipherText, sealed_.Tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>
    /// Crypto-shred: destroy the subject's data key. All content sealed under it becomes
    /// permanently unrecoverable, satisfying GDPR/HIPAA erasure without mutating the
    /// append-only store. Returns true if a key was present and destroyed.
    /// </summary>
    public bool CryptoShred(string subjectId)
    {
        if (_keys.TryRemove(subjectId, out var key))
        {
            CryptographicOperations.ZeroMemory(key);   // wipe the key material in memory
            return true;
        }
        return false;
    }
}

/// <summary>
/// Per-tenant/region data-lifecycle policy (PROJECT_CONTEXT §6): retention window,
/// residency region, and whether opt-in content capture is enabled. Consulted before
/// content is captured or when computing what to purge; keeps residency/retention a
/// config value, not code.
/// </summary>
public sealed record DataLifecyclePolicy
{
    public required string TenantId { get; init; }
    public required string ResidencyRegion { get; init; }     // e.g. "eu", "us"
    public int RetentionDays { get; init; } = 90;
    public bool ContentCaptureEnabled { get; init; } = false;  // opt-in; off by default (PII posture)

    /// <summary>A span older than the retention window is due for purge.</summary>
    public bool IsExpired(DateTimeOffset spanTime, DateTimeOffset now) =>
        (now - spanTime).TotalDays > RetentionDays;

    /// <summary>Residency guard: a region must match the tenant's residency to store there.</summary>
    public bool AllowsRegion(string region) =>
        string.Equals(region, ResidencyRegion, StringComparison.OrdinalIgnoreCase);
}
