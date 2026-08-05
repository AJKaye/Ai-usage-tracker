using System.Text;
using System.Text.Json;
using UsageTracker.Contracts;
using UsageTracker.Normalization;

namespace UsageTracker.Ingestion.Proxy;

/// <summary>
/// The zero-instrumentation proxy archetype (ARCHITECTURE.md §2). Forwards an
/// OpenAI- or Anthropic-shaped request to the upstream provider (over an INJECTED
/// <see cref="HttpClient"/> so it is testable with no real network) and returns the
/// upstream bytes verbatim, plus the canonical <see cref="Span"/> it captured from
/// the response <c>usage</c>. Point an app's base URL here and its usage + cost are
/// tracked with zero app code.
///
/// Handles both the non-streaming JSON body and streaming SSE, where usage is split
/// across events — Anthropic emits input on <c>message_start</c> and final output on
/// <c>message_delta</c>; OpenAI puts final usage on the last chunk (with
/// <c>stream_options.include_usage</c>). Token math (subset-vs-additive) is deferred
/// to the provider-aware <see cref="TokenNormalizerRegistry"/>, so the proxy never
/// double-counts cache/reasoning.
/// </summary>
public sealed class OpenAiCompatibleProxy : IProxyBackend
{
    private readonly HttpClient _upstream;
    private readonly TokenNormalizerRegistry _normalizers;
    private readonly TimeProvider _clock;
    public string Provider { get; }

    public OpenAiCompatibleProxy(string provider, HttpClient upstream, TokenNormalizerRegistry normalizers, TimeProvider? clock = null)
        => (Provider, _upstream, _normalizers, _clock) = (provider, upstream, normalizers, clock ?? TimeProvider.System);

    public async Task<ProxyResult> ForwardAsync(
        string tenantId, ReadOnlyMemory<byte> requestBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        // Forward the request verbatim upstream. The path is taken from the caller's
        // headers (":path") or defaults per provider.
        string path = headers.TryGetValue(":path", out var p) ? p : DefaultPath(Provider);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(requestBody.ToArray()),
        };
        foreach (var (k, v) in headers)
        {
            if (k.StartsWith(':')) continue;                              // pseudo-headers
            if (string.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(v);
                continue;
            }
            req.Headers.TryAddWithoutValidation(k, v);
        }
        if (req.Content.Headers.ContentType is null)
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var resp = await _upstream.SendAsync(req, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var body = Encoding.UTF8.GetString(bytes);

        var raw = LooksLikeSse(body) ? ParseStreaming(body) : ParseJson(body);
        var (usage, model) = raw;

        var normalized = usage is not null ? _normalizers.Normalize(Provider, usage) : null;
        var span = new Span
        {
            TenantId = tenantId,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Llm,
            Provider = Provider,
            ResponseModel = model,
            Granularity = Granularity.Token,
            RawUsage = usage,
            Usage = normalized,
            StartTime = _clock.GetUtcNow(),
            Metadata = new Dictionary<string, string> { ["archetype"] = "proxy" },
        };
        return new ProxyResult(bytes, (int)resp.StatusCode, span);
    }

    private static string DefaultPath(string provider) => provider switch
    {
        "anthropic" => "/v1/messages",
        _ => "/v1/chat/completions",
    };

    private static bool LooksLikeSse(string body)
        => body.StartsWith("event:", StringComparison.Ordinal) || body.StartsWith("data:", StringComparison.Ordinal);

    // --- non-streaming: usage is a single object on the response body ---
    private (TokenUsage? usage, string? model) ParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string? model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
        if (!root.TryGetProperty("usage", out var u)) return (null, model);
        return (ReadUsage(u), model);
    }

    // --- streaming SSE: merge input (message_start) + final output (message_delta),
    //     or take the final chunk's usage (OpenAI include_usage). ---
    private (TokenUsage? usage, string? model) ParseStreaming(string body)
    {
        string? model = null;
        long? input = null, output = null, cacheRead = null, cacheCreate = null, reasoning = null;

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = trimmed["data:".Length..].Trim();
            if (payload is "[DONE]" or "") continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                // model can appear on message_start.message.model or top-level model
                if (model is null)
                {
                    if (root.TryGetProperty("model", out var mm)) model = mm.GetString();
                    else if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("model", out var mm2)) model = mm2.GetString();
                }
                // Anthropic: message_start.message.usage (input) + message_delta.usage (final output)
                JsonElement usageEl = default;
                bool haveUsage = false;
                if (root.TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("usage", out var us1)) { usageEl = us1; haveUsage = true; }
                else if (root.TryGetProperty("usage", out var us2)) { usageEl = us2; haveUsage = true; }
                if (!haveUsage) continue;

                var partial = ReadUsage(usageEl);
                // Merge: keep the max non-null seen for each bucket (input arrives early,
                // output/finals arrive late; either may be repeated).
                input = Max(input, partial.InputTokens);
                output = Max(output, partial.OutputTokens);
                cacheRead = Max(cacheRead, partial.CacheReadInputTokens);
                cacheCreate = Max(cacheCreate, partial.CacheCreationInputTokens);
                reasoning = Max(reasoning, partial.ReasoningOutputTokens);
            }
        }

        if (input is null && output is null) return (null, model);
        return (new TokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreate,
            ReasoningOutputTokens = reasoning,
        }, model);
    }

    private static long? Max(long? a, long? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => Math.Max(a!.Value, b!.Value),
    };

    // Reads either dialect's usage object into raw buckets. OpenAI uses
    // prompt_tokens/completion_tokens (+ *_details); Anthropic uses
    // input_tokens/output_tokens (+ cache_* siblings). The normalizer applies the
    // subset-vs-additive rule per provider afterward.
    private static TokenUsage ReadUsage(JsonElement u)
    {
        long? L(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        long? Nested(string obj, string name) =>
            u.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

        // input: OpenAI prompt_tokens | Anthropic input_tokens
        long? input = L("input_tokens") ?? L("prompt_tokens");
        long? output = L("output_tokens") ?? L("completion_tokens");
        // cache: Anthropic top-level | OpenAI prompt_tokens_details.cached_tokens (read only)
        long? cacheRead = L("cache_read_input_tokens") ?? Nested("prompt_tokens_details", "cached_tokens");
        long? cacheCreate = L("cache_creation_input_tokens");
        long? reasoning = L("reasoning_tokens") ?? Nested("completion_tokens_details", "reasoning_tokens");

        return new TokenUsage
        {
            InputTokens = input,
            OutputTokens = output,
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreate,
            ReasoningOutputTokens = reasoning,
        };
    }
}
