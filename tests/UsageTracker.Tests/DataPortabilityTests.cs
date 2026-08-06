using UsageTracker.Contracts;
using UsageTracker.Portability;
using UsageTracker.Storage.InMemory;
using UsageTracker.Storage.Sqlite;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 10 / Increment 1 — backup/restore + data-export/portability, and the
/// solo→distributed migration substrate. Because the service speaks only IEventStore,
/// an export from one store imports into ANY other unchanged.
/// </summary>
public class DataPortabilityTests
{
    private static Span Costed(string spanId, string provider, decimal cost) => new()
    {
        TenantId = "t", TraceId = "tr", SpanId = spanId, Kind = SpanKind.Llm, Provider = provider,
        Usage = new NormalizedUsage
        {
            InputTokens = 1000, UncachedInputTokens = 1000, CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0, OutputTokens = 500, ReasoningOutputTokens = 0,
        },
        StartTime = DateTimeOffset.UnixEpoch,
        EstimatedCost = new CostBreakdown { TotalCost = cost, Currency = "USD", Components = Array.Empty<CostComponent>(), Tier = "PriceMap" },
    };

    private static async Task<IEventStore> Seeded(IEventStore store, params Span[] spans)
    {
        foreach (var s in spans) await store.AppendAsync(s);
        return store;
    }

    [Fact]
    public async Task Export_then_import_round_trips_spans_and_cost()
    {
        var src = await Seeded(new InMemoryEventStore(), Costed("s1", "anthropic", 0.0175m), Costed("s2", "openai", 0.0080m));
        var svc = new DataPortabilityService(src);

        var json = await svc.ExportJsonAsync("t");

        // Import into a FRESH store — restore/backup scenario.
        var dst = new InMemoryEventStore();
        var result = await new DataPortabilityService(dst).ImportJsonAsync("t", json);

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Skipped);
        var restored = await dst.SummarizeAsync(new SpanQuery { TenantId = "t", Limit = int.MaxValue });
        Assert.Equal(2, restored.SpanCount);
        Assert.Equal(0.0255m, restored.TotalEstimatedCost);   // cost survives the round-trip
    }

    [Fact]
    public async Task Cross_store_migration_sqlite_to_inmemory_preserves_data()
    {
        // The solo→distributed proof: export from the embedded SQLite store, import
        // into a different store impl. (In-memory stands in for the server store here;
        // both satisfy the same IEventStore contract.)
        var sqlite = SqliteEventStore.InMemoryShared("port-" + Guid.NewGuid().ToString("n"));
        await Seeded(sqlite, Costed("s1", "anthropic", 0.02m), Costed("s2", "anthropic", 0.03m), Costed("s3", "openai", 0.05m));

        var json = await new DataPortabilityService(sqlite).ExportJsonAsync("t");
        var target = new InMemoryEventStore();
        var result = await new DataPortabilityService(target).ImportJsonAsync("t", json);

        Assert.Equal(3, result.Imported);
        var s = await target.SummarizeAsync(new SpanQuery { TenantId = "t", Limit = int.MaxValue });
        Assert.Equal(3, s.SpanCount);
        Assert.Equal(0.10m, s.TotalEstimatedCost);
    }

    [Fact]
    public async Task Import_is_idempotent_reimport_is_safe()
    {
        var src = await Seeded(new InMemoryEventStore(), Costed("s1", "anthropic", 0.01m));
        var json = await new DataPortabilityService(src).ExportJsonAsync("t");

        var dst = new InMemoryEventStore();
        var svc = new DataPortabilityService(dst);
        await svc.ImportJsonAsync("t", json);
        await svc.ImportJsonAsync("t", json);   // re-import same bundle

        var s = await dst.SummarizeAsync(new SpanQuery { TenantId = "t", Limit = int.MaxValue });
        Assert.Equal(1, s.SpanCount);            // (tenant, span) key → no duplicate
    }

    [Fact]
    public async Task Import_can_rehome_a_bundle_into_a_different_tenant()
    {
        var src = await Seeded(new InMemoryEventStore(), Costed("s1", "anthropic", 0.01m));
        var json = await new DataPortabilityService(src).ExportJsonAsync("t");

        var dst = new InMemoryEventStore();
        await new DataPortabilityService(dst).ImportJsonAsync("tenant-restored", json);

        Assert.Equal(1, (await dst.SummarizeAsync(new SpanQuery { TenantId = "tenant-restored", Limit = int.MaxValue })).SpanCount);
        Assert.Equal(0, (await dst.SummarizeAsync(new SpanQuery { TenantId = "t", Limit = int.MaxValue })).SpanCount);   // original tenant unaffected
    }

    [Fact]
    public async Task Export_is_tenant_scoped()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync(Costed("s1", "anthropic", 0.01m));                       // tenant "t"
        await store.AppendAsync(Costed("s2", "openai", 0.99m) with { TenantId = "other" });

        var bundle = await new DataPortabilityService(store).ExportAsync("t");
        Assert.Equal(1, bundle.SpanCount);
        Assert.All(bundle.Spans, sp => Assert.Equal("t", sp.TenantId));
    }

    [Fact]
    public async Task Unknown_bundle_format_is_rejected()
    {
        var svc = new DataPortabilityService(new InMemoryEventStore());
        var future = new ExportBundle { Format = 999, Tenant = "t", ExportedAt = DateTimeOffset.UnixEpoch, Spans = Array.Empty<Span>() };
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.ImportAsync("t", future));
    }
}
