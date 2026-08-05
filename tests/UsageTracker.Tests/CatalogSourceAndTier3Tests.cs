using System.Net;
using System.Security.Cryptography;
using System.Text;
using UsageTracker.Contracts;
using UsageTracker.Cost;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 3 / Increment 5 — catalog sources (LiteLLM + injected-HTTP live-sync),
/// the signed offline bundle (D6 air-gap), and Tier-3 tokenize-then-price
/// (ARCHITECTURE.md §4.2/§4.3, §4.1 tier 3). No test touches the network.
/// </summary>
public class CatalogSourceAndTier3Tests
{
    private static NormalizedUsage InOut(long input, long output) => new()
    {
        InputTokens = input, UncachedInputTokens = input,
        CacheReadInputTokens = 0, CacheCreationInputTokens = 0,
        OutputTokens = output, ReasoningOutputTokens = 0,
    };

    private static Span TokenSpan(string model, NormalizedUsage? usage, string? text = null, DateTimeOffset? start = null) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = "sp", Kind = SpanKind.Llm,
        Provider = "p", ResponseModel = model, Usage = usage, EstimationText = text,
        StartTime = start ?? DateTimeOffset.UnixEpoch,
    };

    // --- LiteLLM schema → ModelRate -----------------------------------------------
    [Fact]
    public void LiteLLM_source_parses_model_prices_schema()
    {
        const string json = """
        {
          "sample_spec": { "note": "this non-model row must be skipped" },
          "claude-opus-5": {
            "input_cost_per_token": 0.000005,
            "output_cost_per_token": 0.000025,
            "cache_read_input_token_cost": 0.0000005,
            "cache_creation_input_token_cost": 0.00000625,
            "mode": "chat"
          }
        }
        """;
        var catalog = new PriceCatalog(new LiteLLMCatalogSource(json, "litellm-2026-08"));
        var rate = catalog.Resolve(TokenSpan("claude-opus-5", InOut(1, 1)))!;

        Assert.Equal(0.000005m, rate.InputPerToken);
        Assert.Equal(0.000025m, rate.OutputPerToken);
        Assert.Equal(0.0000005m, rate.CacheReadPerToken);
        Assert.Equal("litellm", rate.SourceId);
        Assert.Equal("litellm-2026-08", rate.CatalogVersion);
    }

    [Fact]
    public void LiteLLM_long_context_column_becomes_a_long_tier_variant()
    {
        const string json = """
        {
          "gemini-3.1-pro": {
            "input_cost_per_token": 0.000002,
            "output_cost_per_token": 0.000012,
            "input_cost_per_token_above_200k_tokens": 0.000004,
            "output_cost_per_token_above_200k_tokens": 0.000024
          }
        }
        """;
        var engine = TieredCostEngine.CreateDefault(new PriceCatalog(new LiteLLMCatalogSource(json, "v")));

        var small = engine.Cost(TokenSpan("gemini-3.1-pro", InOut(1000, 0)))!;
        Assert.Equal(0.002m, small.TotalCost);        // 1000 * 2e-6 (standard)

        var large = engine.Cost(TokenSpan("gemini-3.1-pro", InOut(201_000, 0)))!;
        Assert.Equal(0.804m, large.TotalCost);        // 201000 * 4e-6 (whole request re-rated long)
    }

    // --- injected HTTP handler: live-sync without touching the network ------------
    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public void Http_source_loads_from_injected_handler_no_network()
    {
        const string body = """
        { "gpt-5.6": { "input_cost_per_token": 0.000005, "output_cost_per_token": 0.000015 } }
        """;
        using var http = new HttpClient(new CannedHandler(body));
        var source = new HttpCatalogSource(http, new Uri("https://pricing.invalid/models.json"), "http-v1");
        Assert.Equal("live-sync.http", source.SourceId);         // the source identifies itself
        var catalog = new PriceCatalog(source);

        var rate = catalog.Resolve(TokenSpan("gpt-5.6", InOut(1, 1)))!;
        Assert.Equal(0.000005m, rate.InputPerToken);
        Assert.Equal("http-v1", rate.CatalogVersion);            // version threaded through the HTTP fetch
        // Rates carry the PARSER's schema id (litellm) since HTTP delegates parsing to it.
        Assert.Equal("litellm", rate.SourceId);
    }

    // --- signed offline bundle: good verifies, tampered is rejected ---------------
    [Fact]
    public void Signed_bundle_verifies_and_tampered_bundle_is_rejected()
    {
        const string bundle = """
        { "version": "signed-2026-08", "models": [
          { "model": "claude-opus-5", "input_per_token": 0.000005, "output_per_token": 0.000025 } ] }
        """;
        var bytes = Encoding.UTF8.GetBytes(bundle);

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = ec.SignData(bytes, HashAlgorithmName.SHA256);
        var verifier = EcdsaBundleVerifier.FromSpki(ec.ExportSubjectPublicKeyInfo());

        // Good bundle loads + yields a stable digest.
        var source = new SignedOfflineBundleSource(bytes, signature, verifier);
        Assert.Equal(64, source.Digest.Length);                  // hex SHA-256
        var catalog = new PriceCatalog(source);
        Assert.NotNull(catalog.Resolve(TokenSpan("claude-opus-5", InOut(1, 1))));

        // Tamper one byte of the bundle → signature no longer matches → rejected.
        var tampered = (byte[])bytes.Clone();
        tampered[10] ^= 0xFF;
        Assert.Throws<InvalidOperationException>(
            () => new SignedOfflineBundleSource(tampered, signature, verifier));
    }

    // --- Tier-3 tokenize-then-price -----------------------------------------------
    [Fact]
    public void Tier3_estimates_tokens_from_text_and_prices()
    {
        var catalog = new PriceCatalog(OfflineBundleCatalogSource.Seed());
        var engine = TieredCostEngine.CreateDefault(catalog, TimeProvider.System, new HeuristicTokenizer());

        // 400 chars → ceil(400/4) = 100 tokens; claude-opus-5 input 5e-6 → 0.0005.
        var text = new string('x', 400);
        var cost = engine.Cost(TokenSpan("claude-opus-5", usage: null, text: text))!;

        Assert.Equal("Tokenized", cost.Tier);
        Assert.Equal(0.0005m, cost.TotalCost);
        Assert.Equal("heuristic.chars-div-4", cost.RateSnapshot!.TokenizerId);
    }

    [Fact]
    public void Tier3_unknown_model_with_no_text_falls_through_to_unpriced()
    {
        var catalog = new PriceCatalog(OfflineBundleCatalogSource.Seed());
        var engine = TieredCostEngine.CreateDefault(catalog, TimeProvider.System, new HeuristicTokenizer());

        // No usage, no text → nothing to estimate → Unpriced $0 (honest).
        var cost = engine.Cost(TokenSpan("claude-opus-5", usage: null, text: null))!;
        Assert.Equal("Unpriced", cost.Tier);
        Assert.Equal(0m, cost.TotalCost);
    }

    [Fact]
    public void HeuristicTokenizer_counts_by_quarter_length()
    {
        var tok = new HeuristicTokenizer();
        Assert.Equal(0, tok.CountTokens(""));
        Assert.Equal(1, tok.CountTokens("abc"));      // ceil(3/4)
        Assert.Equal(25, tok.CountTokens(new string('y', 100)));
    }
}
