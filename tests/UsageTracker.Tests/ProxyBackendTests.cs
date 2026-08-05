using System.Net;
using System.Text;
using UsageTracker.Contracts;
using UsageTracker.Ingestion.Proxy;
using UsageTracker.Normalization;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 5 / Increment 1 — the OpenAI-compatible proxy archetype (ARCHITECTURE.md
/// §2). Forwards a request upstream over an INJECTED HttpClient (no real network),
/// returns the bytes verbatim, and captures a canonical Span from the wire usage.
/// Token math is deferred to the provider-aware normalizer (subset vs additive), so
/// cache/reasoning are never double-counted.
/// </summary>
public class ProxyBackendTests
{
    // Canned upstream: returns a fixed body + status, records what it received.
    private sealed class CannedUpstream(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public byte[]? SeenRequest { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenRequest = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static HttpClient Client(HttpMessageHandler h) => new(h) { BaseAddress = new Uri("https://upstream.invalid") };
    private static readonly TokenNormalizerRegistry Norm = TokenNormalizerRegistry.CreateDefault();
    private static readonly Dictionary<string, string> NoHeaders = new();

    // --- OpenAI non-streaming: usage object with prompt/completion + cached subset --
    [Fact]
    public async Task OpenAi_proxy_captures_usage_and_returns_bytes_verbatim()
    {
        // OpenAI: cached_tokens is a SUBSET of prompt_tokens (don't add on top).
        const string body = """
        { "model": "gpt-5.6", "choices": [],
          "usage": { "prompt_tokens": 1000, "completion_tokens": 300,
            "prompt_tokens_details": { "cached_tokens": 600 } } }
        """;
        var upstream = new CannedUpstream(body);
        var proxy = new OpenAiCompatibleProxy("openai", Client(upstream), Norm);

        var reqBytes = Encoding.UTF8.GetBytes("""{"model":"gpt-5.6","messages":[]}""");
        var result = await proxy.ForwardAsync("t", reqBytes, NoHeaders);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(body, Encoding.UTF8.GetString(result.ResponseBody.ToArray()));   // verbatim passthrough
        Assert.Equal(reqBytes, upstream.SeenRequest);                                  // forwarded verbatim

        var s = result.Span;
        Assert.Equal("openai", s.Provider);
        Assert.Equal("gpt-5.6", s.ResponseModel);
        Assert.Equal(1000, s.Usage!.InputTokens);            // subset: cache stays inside input
        Assert.Equal(400, s.Usage!.UncachedInputTokens);     // 1000 - 600
        Assert.Equal(300, s.Usage!.OutputTokens);
        Assert.Equal("proxy", s.Metadata!["archetype"]);
    }

    // --- Anthropic streaming SSE: input on message_start, final output on message_delta
    [Fact]
    public async Task Anthropic_proxy_merges_streaming_usage_across_events()
    {
        // Additive provider: true input = input_tokens + cache_read + cache_creation.
        const string sse = """
        event: message_start
        data: {"type":"message_start","message":{"model":"claude-opus-5","usage":{"input_tokens":200,"cache_read_input_tokens":600,"cache_creation_input_tokens":200,"output_tokens":1}}}

        event: message_delta
        data: {"type":"message_delta","usage":{"output_tokens":500}}

        event: message_stop
        data: {"type":"message_stop"}
        """;
        var proxy = new OpenAiCompatibleProxy("anthropic", Client(new CannedUpstream(sse)), Norm);
        var result = await proxy.ForwardAsync("t", Encoding.UTF8.GetBytes("{}"), NoHeaders);

        var s = result.Span;
        Assert.Equal("claude-opus-5", s.ResponseModel);
        Assert.Equal(1000, s.Usage!.InputTokens);            // additive: 200 + 600 + 200
        Assert.Equal(200, s.Usage!.UncachedInputTokens);     // the reported remainder
        Assert.Equal(500, s.Usage!.OutputTokens);            // final from message_delta (max over 1, 500)
    }

    // --- upstream error status is passed through, no span usage fabricated ---------
    [Fact]
    public async Task Upstream_error_status_is_passed_through()
    {
        var proxy = new OpenAiCompatibleProxy("openai",
            Client(new CannedUpstream("""{"error":"rate_limited"}""", HttpStatusCode.TooManyRequests)), Norm);
        var result = await proxy.ForwardAsync("t", Encoding.UTF8.GetBytes("{}"), NoHeaders);

        Assert.Equal(429, result.StatusCode);
        Assert.Null(result.Span.Usage);                      // no usage object → no fabricated tokens
    }
}
