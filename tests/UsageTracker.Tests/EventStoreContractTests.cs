using UsageTracker.Contracts;
using UsageTracker.Storage.InMemory;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// THE MODULARITY MECHANISM MADE REAL (PROJECT_CONTEXT.md §5; DEVELOPMENT_PLAN
/// "Testing" workstream): an abstract contract-conformance suite for
/// <see cref="IEventStore"/>. Every implementation must pass the SAME assertions,
/// so a store is swappable iff it satisfies this suite. The ClickHouse store
/// (Phase 1) will subclass this and inherit every test — that is how we prove
/// the seam holds rather than hoping it does.
/// </summary>
public abstract class EventStoreContractTests
{
    protected abstract IEventStore CreateStore();

    private static Span MakeSpan(string tenant, string spanId, string provider = "openai",
        string model = "gpt-5.6", long input = 100, long output = 50) => new()
    {
        TenantId = tenant, TraceId = "tr-" + spanId, SpanId = spanId, Kind = SpanKind.Llm,
        Provider = provider, ResponseModel = model,
        Usage = new NormalizedUsage
        {
            InputTokens = input, UncachedInputTokens = input, CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0, OutputTokens = output, ReasoningOutputTokens = 0,
        },
        EstimatedCost = new CostBreakdown
        {
            TotalCost = 0.01m, Currency = "USD", Components = Array.Empty<CostComponent>(), Tier = "PriceMap",
        },
        StartTime = DateTimeOffset.UnixEpoch.AddMinutes(int.Parse(spanId.AsSpan(^1..))),
    };

    [Fact]
    public async Task Append_then_Get_roundtrips()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeSpan("t1", "span-1"));
        var got = await store.GetAsync("t1", "span-1");
        Assert.NotNull(got);
        Assert.Equal("span-1", got!.SpanId);
    }

    [Fact]
    public async Task Get_is_tenant_scoped_no_cross_tenant_read()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeSpan("tenant-a", "span-2"));
        // A different tenant must never see it.
        Assert.Null(await store.GetAsync("tenant-b", "span-2"));
    }

    [Fact]
    public async Task Append_is_idempotent_by_span_id()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeSpan("t1", "span-3", output: 50));
        await store.AppendAsync(MakeSpan("t1", "span-3", output: 999)); // same id, resend
        var results = await store.QueryAsync(new SpanQuery { TenantId = "t1" });
        Assert.Single(results, s => s.SpanId == "span-3");  // not duplicated
    }

    [Fact]
    public async Task Query_filters_by_provider_within_tenant()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeSpan("t2", "span-4", provider: "openai"));
        await store.AppendAsync(MakeSpan("t2", "span-5", provider: "anthropic"));
        var openai = await store.QueryAsync(new SpanQuery { TenantId = "t2", Provider = "openai" });
        Assert.All(openai, s => Assert.Equal("openai", s.Provider));
        Assert.Contains(openai, s => s.SpanId == "span-4");
        Assert.DoesNotContain(openai, s => s.SpanId == "span-5");
    }

    [Fact]
    public async Task Summarize_rolls_up_tokens_and_cost_by_provider_and_model()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeSpan("t3", "span-6", provider: "openai", model: "gpt-5.6"));
        await store.AppendAsync(MakeSpan("t3", "span-7", provider: "anthropic", model: "claude-opus-5"));
        var sum = await store.SummarizeAsync(new SpanQuery { TenantId = "t3" });
        Assert.Equal(2, sum.SpanCount);
        Assert.Equal(0.02m, sum.TotalEstimatedCost);
        Assert.True(sum.CostByProvider.ContainsKey("openai"));
        Assert.True(sum.CostByModel.ContainsKey("claude-opus-5"));
    }

    [Fact]
    public async Task Summarize_is_tenant_scoped()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeSpan("owner", "span-8"));
        var sum = await store.SummarizeAsync(new SpanQuery { TenantId = "stranger" });
        Assert.Equal(0, sum.SpanCount);
        Assert.Equal(0m, sum.TotalEstimatedCost);
    }
}

/// <summary>Runs the full contract suite against the in-memory implementation.</summary>
public sealed class InMemoryEventStoreContractTests : EventStoreContractTests
{
    protected override IEventStore CreateStore() => new InMemoryEventStore();
}
