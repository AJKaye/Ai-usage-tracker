using System.Security.Cryptography;
using UsageTracker.Contracts;

namespace UsageTracker.Cost;

/// <summary>
/// Verifies a signed offline pricing bundle with a detached ECDSA-P256-over-SHA256
/// signature (ARCHITECTURE.md §4.3, D6/FedRAMP air-gap). Fully offline: verification
/// uses only a configured public key — no network, no external service. A tampered
/// bundle (or a signature made with the wrong key) is rejected.
/// </summary>
public sealed class EcdsaBundleVerifier : IBundleVerifier
{
    private readonly ECDsa _publicKey;

    public EcdsaBundleVerifier(ECDsa publicKey) => _publicKey = publicKey;

    /// <summary>Build a verifier from a SubjectPublicKeyInfo (SPKI) DER blob.</summary>
    public static EcdsaBundleVerifier FromSpki(byte[] spki)
    {
        var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(spki, out _);
        return new EcdsaBundleVerifier(ec);
    }

    public string VerifyAndDigest(byte[] bundleBytes, byte[] signature)
    {
        if (!_publicKey.VerifyData(bundleBytes, signature, HashAlgorithmName.SHA256))
            throw new InvalidOperationException(
                "offline pricing bundle signature is invalid — refusing to load (D6/air-gap integrity gate).");
        return Convert.ToHexStringLower(SHA256.HashData(bundleBytes));
    }
}

/// <summary>
/// Loads an offline bundle only after its detached signature verifies, then delegates
/// parsing to <see cref="OfflineBundleCatalogSource"/>. The verified SHA-256 digest is
/// exposed so it can be threaded into the rate snapshot (proof an air-gap recompute
/// used signature-verified rates). The unsigned <see cref="OfflineBundleCatalogSource.Seed"/>
/// path stays available for dev, but production/air-gap MUST use this.
/// </summary>
public sealed class SignedOfflineBundleSource : IPriceCatalogSource
{
    private readonly OfflineBundleCatalogSource _inner;
    public string SourceId => "offline-bundle-signed";
    public string Digest { get; }

    public SignedOfflineBundleSource(byte[] bundleBytes, byte[] signature, IBundleVerifier verifier)
    {
        Digest = verifier.VerifyAndDigest(bundleBytes, signature);   // throws if tampered
        _inner = new OfflineBundleCatalogSource(System.Text.Encoding.UTF8.GetString(bundleBytes));
    }

    public IReadOnlyList<ModelRate> Load() => _inner.Load();
    public IReadOnlyList<UnitRate> LoadUnits() => _inner.LoadUnits();
    public IReadOnlyDictionary<string, decimal> LoadToolSurcharges() => _inner.LoadToolSurcharges();
}

/// <summary>
/// Heuristic tokenizer (ARCHITECTURE.md §4.1 tier 3): approximates token count as
/// ~1 token per 4 characters — the documented rule-of-thumb. Deliberately crude and
/// clearly labelled; a real BPE tokenizer (tiktoken/Claude) plugs in behind
/// <see cref="ITokenizer"/> without touching callers.
/// </summary>
public sealed class HeuristicTokenizer : ITokenizer
{
    public string Id => "heuristic.chars-div-4";
    public long CountTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (long)Math.Ceiling(text.Length / 4.0);
}
