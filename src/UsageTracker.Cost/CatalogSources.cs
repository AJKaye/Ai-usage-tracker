using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Cost;

/// <summary>
/// Parses LiteLLM's <c>model_prices_and_context_window.json</c> schema — the de-facto
/// public price map (ARCHITECTURE.md §4.2) — into <see cref="ModelRate"/>s. The file
/// is a JSON object keyed by model name; each value carries per-token costs. A
/// model with an <c>*_above_200k_tokens</c> input cost yields a second, long-context
/// rate variant (§5 #5) so the composite-key catalog re-rates large prompts.
/// </summary>
public sealed class LiteLLMCatalogSource : IPriceCatalogSource
{
    private readonly string _json;
    private readonly string _version;
    public string SourceId => "litellm";

    public LiteLLMCatalogSource(string json, string version)
        => (_json, _version) = (json, version);

    public static LiteLLMCatalogSource FromFile(string path, string version)
        => new(File.ReadAllText(path), version);

    public IReadOnlyList<ModelRate> Load()
    {
        using var doc = JsonDocument.Parse(_json);
        var list = new List<ModelRate>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var m = prop.Value;
            if (m.ValueKind != JsonValueKind.Object) continue;              // skip "sample_spec" etc.
            if (!m.TryGetProperty("input_cost_per_token", out var inTok)) continue;

            decimal input = inTok.GetDecimal();
            decimal output = m.TryGetProperty("output_cost_per_token", out var o) ? o.GetDecimal() : 0m;
            decimal cacheRead = m.TryGetProperty("cache_read_input_token_cost", out var cr) ? cr.GetDecimal() : 0m;
            decimal cacheCreate = m.TryGetProperty("cache_creation_input_token_cost", out var cc) ? cc.GetDecimal() : 0m;
            decimal? reasoning = m.TryGetProperty("output_cost_per_reasoning_token", out var rp) ? rp.GetDecimal() : null;

            list.Add(new ModelRate
            {
                Model = prop.Name,
                InputPerToken = input,
                OutputPerToken = output,
                CacheReadPerToken = cacheRead,
                CacheCreationPerToken = cacheCreate,
                ReasoningPerToken = reasoning,
                CatalogVersion = _version,
                SourceId = SourceId,
            });

            // Long-context column: a higher input (and optional output) cost that
            // applies once the prompt crosses 200k, to the WHOLE request (§5 #5).
            if (m.TryGetProperty("input_cost_per_token_above_200k_tokens", out var inLong))
            {
                list.Add(new ModelRate
                {
                    Model = prop.Name,
                    InputPerToken = inLong.GetDecimal(),
                    OutputPerToken = m.TryGetProperty("output_cost_per_token_above_200k_tokens", out var oLong)
                        ? oLong.GetDecimal() : output,
                    CacheReadPerToken = cacheRead,
                    CacheCreationPerToken = cacheCreate,
                    ReasoningPerToken = reasoning,
                    CatalogVersion = _version,
                    SourceId = SourceId,
                    ContextTier = ContextTier.Long,
                    LongContextThresholdTokens = 200_000,
                });
                // Mark the base row as the Standard tier so the two don't both match.
                var idx = list.Count - 2;
                list[idx] = list[idx] with { ContextTier = ContextTier.Standard, LongContextThresholdTokens = 200_000 };
            }
        }
        return list;
    }
}

/// <summary>
/// Live-sync catalog source that fetches a LiteLLM-format price map over HTTP
/// (ARCHITECTURE.md §4.3). The <see cref="HttpClient"/> is injected, so tests feed
/// a canned <see cref="HttpMessageHandler"/> and NO test ever touches the network.
/// In air-gapped mode this source is simply not selected (the offline bundle is).
/// </summary>
public sealed class HttpCatalogSource : IPriceCatalogSource
{
    private readonly HttpClient _http;
    private readonly Uri _uri;
    private readonly string _version;
    private readonly IEgressGuard? _egress;
    public string SourceId => "live-sync.http";

    public HttpCatalogSource(HttpClient http, Uri uri, string version, IEgressGuard? egress = null)
        => (_http, _uri, _version, _egress) = (http, uri, version, egress);

    public IReadOnlyList<ModelRate> Load()
    {
        // Air-gap gate: fail closed at the call site before any outbound request.
        _egress?.AssertEgressAllowed(_uri.Host, "live-sync price catalog");
        // Synchronous over the injected client — sources load once at startup, off
        // the hot path. GetAwaiter().GetResult() is acceptable for a boot-time pull.
        var json = _http.GetStringAsync(_uri).GetAwaiter().GetResult();
        return new LiteLLMCatalogSource(json, _version).Load();
    }
}
