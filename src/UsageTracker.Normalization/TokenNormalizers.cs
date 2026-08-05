using UsageTracker.Contracts;

namespace UsageTracker.Normalization;

/// <summary>
/// SUBSET-family normalizer: OpenAI &amp; Google (and OpenAI-compatible gateways).
/// Here <c>input_tokens</c> is the WHOLE prompt and cached/reasoning counts are
/// SUBSETS already inside the parent total — so we must NOT add them on top.
/// Uncached input = input − cache-read − cache-creation.
/// (ARCHITECTURE.md §3.4 — the #1 mispricing bug if you get the direction wrong.)
/// </summary>
public sealed class SubsetTokenNormalizer : ITokenNormalizer
{
    private static readonly HashSet<string> Providers =
        new(StringComparer.OrdinalIgnoreCase) { "openai", "gcp.gemini", "gcp.vertex_ai", "gcp.gen_ai", "azure.ai.openai" };

    public bool Handles(string provider) => Providers.Contains(provider);

    public NormalizedUsage Normalize(string provider, TokenUsage raw)
    {
        long input = raw.InputTokens ?? 0;
        long output = raw.OutputTokens ?? 0;
        long cacheRead = raw.CacheReadInputTokens ?? 0;
        long cacheCreate = raw.CacheCreationInputTokens ?? 0;
        long reasoning = raw.ReasoningOutputTokens ?? 0;

        // cached tokens are already inside `input`; derive the base-rate remainder.
        long uncached = Math.Max(0, input - cacheRead - cacheCreate);

        return new NormalizedUsage
        {
            InputTokens = input,
            UncachedInputTokens = uncached,
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreate,
            OutputTokens = output,                 // reasoning already inside output
            ReasoningOutputTokens = reasoning,
            AudioTokens = raw.AudioTokens ?? 0,
            ImageTokens = raw.ImageTokens ?? 0,
        };
    }
}

/// <summary>
/// ADDITIVE-family normalizer: Anthropic &amp; Bedrock. Here <c>input_tokens</c> is
/// the UNCACHED REMAINDER only — cache-read and cache-creation are separate and
/// must be ADDED BACK to get the true prompt size
/// (true input = input + cache_read + cache_creation). This is the exact
/// inverse of the subset family and the rule the OTel Anthropic convention encodes.
/// </summary>
public sealed class AdditiveTokenNormalizer : ITokenNormalizer
{
    private static readonly HashSet<string> Providers =
        new(StringComparer.OrdinalIgnoreCase) { "anthropic", "aws.bedrock" };

    public bool Handles(string provider) => Providers.Contains(provider);

    public NormalizedUsage Normalize(string provider, TokenUsage raw)
    {
        long uncached = raw.InputTokens ?? 0;      // Anthropic input_tokens = uncached remainder
        long cacheRead = raw.CacheReadInputTokens ?? 0;
        long cacheCreate = raw.CacheCreationInputTokens ?? 0;
        long output = raw.OutputTokens ?? 0;
        long reasoning = raw.ReasoningOutputTokens ?? 0;

        long trueInput = uncached + cacheRead + cacheCreate;   // add the cache back

        return new NormalizedUsage
        {
            InputTokens = trueInput,
            UncachedInputTokens = uncached,
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreate,
            OutputTokens = output,
            ReasoningOutputTokens = reasoning,
            AudioTokens = raw.AudioTokens ?? 0,
            ImageTokens = raw.ImageTokens ?? 0,
        };
    }
}

/// <summary>
/// Fallback when the provider is unknown: trust the reported totals as-is, but
/// still derive a defensible uncached figure. Flagged distinct so an unknown
/// provider is visible rather than silently mis-bucketed.
/// </summary>
public sealed class PassthroughTokenNormalizer : ITokenNormalizer
{
    public bool Handles(string provider) => true; // last-resort

    public NormalizedUsage Normalize(string provider, TokenUsage raw)
    {
        long input = raw.InputTokens ?? 0;
        long cacheRead = raw.CacheReadInputTokens ?? 0;
        long cacheCreate = raw.CacheCreationInputTokens ?? 0;
        return new NormalizedUsage
        {
            InputTokens = input,
            UncachedInputTokens = Math.Max(0, input - cacheRead - cacheCreate),
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreate,
            OutputTokens = raw.OutputTokens ?? 0,
            ReasoningOutputTokens = raw.ReasoningOutputTokens ?? 0,
            AudioTokens = raw.AudioTokens ?? 0,
            ImageTokens = raw.ImageTokens ?? 0,
        };
    }
}

/// <summary>
/// Routes a raw usage payload to the first registered normalizer that handles
/// the provider, falling back to passthrough. This is the single entry point the
/// ingest path calls; adding a new provider family = adding a normalizer, no
/// change to callers.
/// </summary>
public sealed class TokenNormalizerRegistry
{
    private readonly IReadOnlyList<ITokenNormalizer> _specific;
    private readonly ITokenNormalizer _fallback;

    public TokenNormalizerRegistry(IEnumerable<ITokenNormalizer> normalizers)
    {
        // specific handlers first; passthrough (Handles==always true) kept as fallback
        var all = normalizers.ToList();
        _fallback = all.OfType<PassthroughTokenNormalizer>().FirstOrDefault() ?? new PassthroughTokenNormalizer();
        _specific = all.Where(n => n is not PassthroughTokenNormalizer).ToList();
    }

    /// <summary>Convenience factory wiring the built-in families.</summary>
    public static TokenNormalizerRegistry CreateDefault() =>
        new(new ITokenNormalizer[]
        {
            new SubsetTokenNormalizer(),
            new AdditiveTokenNormalizer(),
            new PassthroughTokenNormalizer(),
        });

    public NormalizedUsage Normalize(string? provider, TokenUsage raw)
    {
        provider ??= string.Empty;
        foreach (var n in _specific)
            if (n.Handles(provider))
                return n.Normalize(provider, raw);
        return _fallback.Normalize(provider, raw);
    }
}
