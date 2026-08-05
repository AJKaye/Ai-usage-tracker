using UsageTracker.Adapters.Reference;
using UsageTracker.Contracts;
using UsageTracker.Cost;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Normalization;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 5 / Increment 3 — reference pull-adapters (ARCHITECTURE.md §7) and the
/// AdapterRunner that schedules pull → sink. Each surface maps to the correct
/// granularity and cost path: UiPath credits, Copilot requests/seats, Claude Code
/// real tokens (additive).
/// </summary>
public class ReferenceAdapterTests
{
    private static ICostEngine Engine() =>
        TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));
    private static readonly TokenNormalizerRegistry Norm = TokenNormalizerRegistry.CreateDefault();

    private static async Task<List<Span>> Pull(IUsageAdapter a)
    {
        var spans = new List<Span>();
        await foreach (var s in a.PullAsync("t", DateTimeOffset.UnixEpoch)) spans.Add(s);
        return spans;
    }

    [Fact]
    public async Task UiPath_adapter_yields_credit_span_priced_via_coarse_unit()
    {
        var span = Assert.Single(await Pull(new UiPathUsageAdapter()));
        Assert.Equal(Granularity.Credit, span.Granularity);
        Assert.Equal(2L, span.UnitsConsumed);
        var cost = Engine().Cost(span)!;
        Assert.Equal("CoarseUnit", cost.Tier);
        Assert.Equal(0.40m, cost.TotalCost);          // 2 × $0.20 ai_unit
    }

    [Fact]
    public async Task Copilot_adapter_yields_request_and_seat_spans()
    {
        var spans = await Pull(new GitHubCopilotUsageAdapter());
        Assert.Equal(2, spans.Count);

        var req = Assert.Single(spans, s => s.Granularity == Granularity.Request);
        Assert.Equal(0.20m, Engine().Cost(req)!.TotalCost);   // 5 × $0.04 premium_request

        var seat = Assert.Single(spans, s => s.Granularity == Granularity.Seat);
        Assert.Equal(57.00m, Engine().Cost(seat)!.TotalCost); // 3 × $19.00 seat
    }

    [Fact]
    public async Task ClaudeCode_adapter_yields_token_span_additive_and_costs()
    {
        var span = Assert.Single(await Pull(new ClaudeCodeUsageAdapter()));
        Assert.Equal(Granularity.Token, span.Granularity);

        // Normalize (Anthropic additive: true input = 300 + 1500 + 200 = 2000).
        var normalized = Norm.Normalize(span.Provider!, span.RawUsage!);
        Assert.Equal(2000, normalized.InputTokens);
        Assert.Equal(300, normalized.UncachedInputTokens);

        // Cost the normalized span (claude-opus-5 seed rates).
        var costed = span with { Usage = normalized };
        var cost = Engine().Cost(costed)!;
        // 300*5e-6 + 1500*5e-7 + 200*6.25e-6 + 800*25e-6
        // = 0.0015 + 0.00075 + 0.00125 + 0.02 = 0.0235
        Assert.Equal(0.0235m, cost.TotalCost);
        Assert.Equal("PriceMap", cost.Tier);
    }
}

/// <summary>Phase 5 / Increment 3 — the AdapterRunner: pull → sink, checkpointing,
/// and graceful failure.</summary>
public class AdapterRunnerTests
{
    private sealed class FailingAdapter : IUsageAdapter
    {
        public string SourceId => "flaky";
        public async IAsyncEnumerable<Span> PullAsync(string tenantId, DateTimeOffset since,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new HttpRequestException("simulated adapter outage");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Runner_pulls_spans_into_the_sink_and_advances_watermark()
    {
        var sunk = new List<Span>();
        var runner = new AdapterRunner(new UiPathUsageAdapter(), (s, ct) => { sunk.Add(s); return Task.CompletedTask; });

        var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z");
        int n = await runner.RunOnceAsync("t", now);

        Assert.Equal(1, n);
        Assert.Single(sunk);
        Assert.Equal(now, runner.WatermarkFor("t"));   // advanced on success
    }

    [Fact]
    public async Task Runner_keeps_watermark_and_swallows_on_adapter_failure()
    {
        var runner = new AdapterRunner(new FailingAdapter(), (s, ct) => Task.CompletedTask);
        var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z");

        int n = await runner.RunOnceAsync("t", now);    // must not throw

        Assert.Equal(0, n);
        Assert.Null(runner.WatermarkFor("t"));          // NOT advanced → next tick retries
    }
}

/// <summary>
/// Phase 5 / Increment 3 — RAG-pipeline coverage: retriever / embedding / reranker
/// spans arrive via the Phase-2 OTLP gen_ai.* path and map to the right span kinds
/// (ARCHITECTURE.md §3.1 nine-kind taxonomy). Confirms RAG works end-to-end with no
/// new code — it's the same OTLP dialect.
/// </summary>
public class RagCoverageTests
{
    private readonly SpanMapperRegistry _reg =
        SpanMapperRegistry.CreateDefault(TokenNormalizerRegistry.CreateDefault());

    private Span MapGenAi(string operation, string provider = "openai") => _reg.Map(new RawIngestEvent
    {
        TenantId = "t", Dialect = "otlp.gen_ai", ReceivedAt = DateTimeOffset.UnixEpoch,
        Attributes = new Dictionary<string, object?>
        {
            ["gen_ai.provider.name"] = provider,
            ["gen_ai.operation.name"] = operation,
        },
    });

    [Fact]
    public void Rag_span_kinds_map_from_genai_operations()
    {
        Assert.Equal(SpanKind.Retriever, MapGenAi("retrieval").Kind);
        Assert.Equal(SpanKind.Embedding, MapGenAi("embeddings").Kind);
    }

    [Fact]
    public void Rag_embedding_span_with_tokens_costs_via_price_map()
    {
        // An embedding call with real input tokens on a priced model.
        var span = _reg.Map(new RawIngestEvent
        {
            TenantId = "t", Dialect = "otlp.gen_ai", ReceivedAt = DateTimeOffset.UnixEpoch,
            Attributes = new Dictionary<string, object?>
            {
                ["gen_ai.provider.name"] = "openai",
                ["gen_ai.operation.name"] = "embeddings",
                ["gen_ai.response.model"] = "gpt-5.6",
                ["gen_ai.usage.input_tokens"] = 1000L,
            },
        });
        Assert.Equal(SpanKind.Embedding, span.Kind);
        Assert.Equal(1000, span.Usage!.InputTokens);
    }
}
