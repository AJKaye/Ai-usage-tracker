using UsageTracker.Contracts;
using UsageTracker.Cost;
using UsageTracker.Ingestion.Api;
using UsageTracker.FinOps;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Normalization;
using UsageTracker.Reconciliation;
using UsageTracker.Storage.InMemory;
using UsageTracker.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// --- DEPLOYMENT PROFILE (the "runs anywhere" knob) -------------------------
// USAGETRACKER__PROFILE selects the backend tier. Default is "solo": an embedded
// SQLite store that needs ZERO infrastructure — the mode the downloadable .exe
// runs in. "standard"/"distributed" swap in server backends (Postgres / ClickHouse
// + Kafka) behind the same IEventStore contract, so it's a config change only.
var profile = (Environment.GetEnvironmentVariable("USAGETRACKER__PROFILE")
    ?? builder.Configuration["Profile"] ?? "solo").Trim().ToLowerInvariant();

// --- composition root: wire the replaceable modules behind their contracts ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TokenNormalizerRegistry.CreateDefault());
builder.Services.AddSingleton<IPriceCatalogSource>(_ => OfflineBundleCatalogSource.Seed());
builder.Services.AddSingleton<IPriceCatalog>(sp => new PriceCatalog(sp.GetRequiredService<IPriceCatalogSource>()));
builder.Services.AddSingleton<ICostEngine>(sp => TieredCostEngine.CreateDefault(
    sp.GetRequiredService<IPriceCatalog>(), sp.GetRequiredService<TimeProvider>()));
// Historical recompute (ARCHITECTURE §4.1 / §5 #14): reproduce a stored event's cost
// from its rate snapshot, or re-price it against today's live catalog.
builder.Services.AddSingleton<ICostRecomputer>(sp =>
    new SnapshotCostRecomputer(sp.GetRequiredService<ICostEngine>()));

// --- Phase 4: reconciliation (estimated vs realized billing) ----------------
// Billing connectors are OPTIONAL: none are registered by default, so the solo
// / air-gap build reconciles nothing external and the estimate stands (the
// reconciler flags ReconciledAgainstBilling=false). A deployment with billing
// credentials registers OpenAiBillingConnector / AnthropicBillingConnector here.
builder.Services.AddSingleton<IReconciliationStore, InMemoryReconciliationStore>();
builder.Services.AddSingleton<IReconciler>(sp => new CostReconciler(
    sp.GetRequiredService<IEventStore>(),
    sp.GetServices<IBillingConnector>(),
    sp.GetRequiredService<IReconciliationStore>(),
    msg => sp.GetRequiredService<ILoggerFactory>().CreateLogger("reconciler").LogWarning("{Msg}", msg)));

// --- Phase 6: FinOps serving (allocation / unit economics / FOCUS / scores) --
builder.Services.AddSingleton<IScoreSink, InMemoryScoreSink>();

// The ONLY line that changes per deployment tier — every profile satisfies IEventStore.
builder.Services.AddSingleton<IEventStore>(_ => profile switch
{
    // Embedded, durable, zero-infra — the .exe default. DB path is configurable;
    // defaults to a file next to the executable so a plain double-click just works.
    "solo" or "embedded" => SqliteEventStore.ForFile(
        Environment.GetEnvironmentVariable("USAGETRACKER__DB")
        ?? Path.Combine(AppContext.BaseDirectory, "usage-tracker.db")),

    // Volatile — for tests / throwaway runs.
    "ephemeral" or "test" => new InMemoryEventStore(),

    // Server tiers: backends land in Phase 1 continuation (need Docker/infra).
    // Fail fast with a clear message rather than silently mis-storing.
    "standard" => throw new NotSupportedException(
        "profile 'standard' (Postgres) is authored against IRelationalStore but its IEventStore wiring is not complete yet — use 'solo' for the zero-infra build."),
    "distributed" => throw new NotSupportedException(
        "profile 'distributed' (ClickHouse+Kafka+Postgres) requires the server backends + infra (Docker/K8s) — use 'solo' for the zero-infra build."),

    _ => throw new ArgumentException($"unknown USAGETRACKER__PROFILE '{profile}' (expected solo|ephemeral|standard|distributed)"),
});

builder.Services.AddSingleton<IngestionService>();

// --- Phase 2: OTLP ingestion — mapper registry + async hot-path channel -----
// Span mapping (wire → canonical) runs synchronously on the request (cheap,
// pure); cost + persist run on the background consumer draining the channel.
builder.Services.AddSingleton(sp =>
    SpanMapperRegistry.CreateDefault(sp.GetRequiredService<TokenNormalizerRegistry>()));
builder.Services.AddSingleton<ChannelIngest>();
builder.Services.AddSingleton<IIngestChannel>(sp => sp.GetRequiredService<ChannelIngest>());
builder.Services.AddHostedService<IngestConsumer>();

var app = builder.Build();

app.Logger.LogInformation("AI Usage Tracker starting — profile='{Profile}'", profile);

// --- tenant resolution (slice: a header; real system: OIDC/mTLS + ITenantResolver) ---
static string Tenant(HttpRequest r) =>
    r.Headers.TryGetValue("X-Tenant-Id", out var v) && !string.IsNullOrWhiteSpace(v)
        ? v.ToString() : "default";

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ingestion-api", version = "0.1.0" }));

// --- Phase 2: OTLP/HTTP trace ingestion (the real OpenTelemetry wire shape) ---
// Accepts an ExportTraceServiceRequest JSON body (resourceSpans→scopeSpans→spans
// with gen_ai.* attributes), maps each span to canonical, and enqueues on the
// async hot path. Heavy work (cost + persist) happens on the background consumer,
// so this returns fast (the SLO seam). Oversized bodies are rejected (413) with
// no partial ingest. Query the results via /v1/spans and /v1/summary as usual.
const long MaxOtlpBytes = 32L * 1024 * 1024;   // 32 MB cap — reject, don't truncate
app.MapPost("/v1/traces", async (HttpRequest req, SpanMapperRegistry mappers, IIngestChannel channel, TimeProvider clock, CancellationToken ct) =>
{
    if (req.ContentLength is > MaxOtlpBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    string body;
    using (var reader = new StreamReader(req.Body))
        body = await reader.ReadToEndAsync(ct);
    if (body.Length == 0)
        return Results.BadRequest(new { error = "empty body" });

    IReadOnlyList<RawIngestEvent> raws;
    try
    {
        raws = OtlpTraceParser.Parse(body, Tenant(req), clock.GetUtcNow());
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = "invalid OTLP JSON", detail = ex.Message });
    }

    int accepted = 0, skipped = 0;
    foreach (var raw in raws)
    {
        if (!mappers.CanMap(raw.Dialect)) { skipped++; continue; }
        await channel.EnqueueAsync(mappers.Map(raw), ct);
        accepted++;
    }
    // 202: accepted for async processing (results become queryable shortly after).
    return Results.Accepted(value: new { accepted, skipped });
});

// --- Phase 5: CloudEvents 1.0 usage-event API (coarse surfaces) ---------------
// Accepts a CloudEvents envelope for coarse usage (RPA "AI units", seats, premium
// requests), maps it via the CloudEvent dialect → canonical span (credit/seat/
// request granularity), and enqueues on the same async hot path. Priced via the
// per-unit (CoarseUnit) cost tier, not token math.
app.MapPost("/v1/events", async (HttpRequest req, SpanMapperRegistry mappers, IIngestChannel channel, TimeProvider clock, CancellationToken ct) =>
{
    string body;
    using (var reader = new StreamReader(req.Body))
        body = await reader.ReadToEndAsync(ct);
    if (body.Length == 0)
        return Results.BadRequest(new { error = "empty body" });

    RawIngestEvent raw;
    try
    {
        raw = CloudEventParser.Parse(body, Tenant(req), clock.GetUtcNow());
    }
    catch (System.Text.Json.JsonException ex)
    {
        return Results.BadRequest(new { error = "invalid CloudEvent", detail = ex.Message });
    }

    if (!mappers.CanMap(raw.Dialect))
        return Results.BadRequest(new { error = $"no mapper for dialect '{raw.Dialect}'" });
    await channel.EnqueueAsync(mappers.Map(raw), ct);
    return Results.Accepted(value: new { accepted = 1 });
});

// Ingest one gen_ai.* event → normalized, costed, stored. Returns the canonical span.
app.MapPost("/v1/ingest", async (HttpRequest req, IngestEventDto dto, IngestionService ingest, CancellationToken ct) =>
{
    var span = await ingest.IngestAsync(Tenant(req), dto, ct);
    return Results.Ok(new
    {
        span.SpanId,
        span.TraceId,
        provider = span.Provider,
        model = span.ResponseModel ?? span.RequestModel,
        usage = span.Usage,
        cost = span.EstimatedCost,
    });
});

// Fetch one span back.
app.MapGet("/v1/spans/{spanId}", async (HttpRequest req, string spanId, IEventStore store, CancellationToken ct) =>
{
    var span = await store.GetAsync(Tenant(req), spanId, ct);
    return span is null ? Results.NotFound() : Results.Ok(span);
});

// Recompute a stored span's cost from its rate snapshot (stable) or against the
// live catalog (what it costs today) — the §5 #14 historical-recompute surface.
app.MapGet("/v1/spans/{spanId}/recompute", async (HttpRequest req, string spanId, string? mode,
        IEventStore store, ICostRecomputer rc, CancellationToken ct) =>
{
    var span = await store.GetAsync(Tenant(req), spanId, ct);
    if (span is null) return Results.NotFound();
    var cost = string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase)
        ? rc.RecomputeFromLiveCatalog(span)
        : rc.RecomputeFromSnapshot(span);
    return Results.Ok(new { spanId, mode = mode ?? "snapshot", original = span.EstimatedCost, recomputed = cost });
});

// Query spans for the tenant.
app.MapGet("/v1/spans", async (HttpRequest req, IEventStore store, string? provider, string? traceId, CancellationToken ct) =>
{
    var spans = await store.QueryAsync(new SpanQuery
    {
        TenantId = Tenant(req), Provider = provider, TraceId = traceId, Limit = 200,
    }, ct);
    return Results.Ok(spans);
});

// Rolled-up cost/usage summary — the seed of the dashboards.
app.MapGet("/v1/summary", async (HttpRequest req, IEventStore store, CancellationToken ct) =>
{
    var summary = await store.SummarizeAsync(new SpanQuery { TenantId = Tenant(req), Limit = int.MaxValue }, ct);
    return Results.Ok(summary);
});

// --- Phase 4: reconcile a tenant-day (estimated vs realized) + read the result ---
// POST triggers a reconciliation for ?day=YYYY-MM-DD (defaults to today UTC),
// pulling any configured billing connectors; GET returns the stored result.
// In the zero-infra build with no connectors, ReconciledAgainstBilling is false
// and the estimate stands — surfaced, not silently zeroed.
app.MapPost("/v1/reconcile", async (HttpRequest req, string? day, IReconciler reconciler, TimeProvider clock, CancellationToken ct) =>
{
    var d = day is not null ? DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)
                            : DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var result = await reconciler.ReconcileAsync(Tenant(req), d, ct);
    return Results.Ok(result);
});

app.MapGet("/v1/reconcile", async (HttpRequest req, string? day, IReconciliationStore store, TimeProvider clock, CancellationToken ct) =>
{
    var d = day is not null ? DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)
                            : DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var result = await store.GetAsync(Tenant(req), d, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

// --- Phase 6: FinOps serving API (the query layer the SPA + integrators consume) ---
// All read over the tenant's spans; the heavy analytics run in-process (solo) or
// could push down to ClickHouse in the scale tier behind the same IEventStore query.

// Cost allocation by any captured dimension (tag-free): team|user|session|model|
// provider|environment|kind, or a Metadata key (feature/agent/mcp.session).
app.MapGet("/v1/allocation", async (HttpRequest req, string? dimension, IEventStore store, CancellationToken ct) =>
{
    var spans = await store.QueryAsync(new SpanQuery { TenantId = Tenant(req), Limit = int.MaxValue }, ct);
    var strategy = new DimensionAllocationStrategy(string.IsNullOrWhiteSpace(dimension) ? "provider" : dimension);
    var buckets = strategy.Allocate(spans);
    return Results.Ok(new { dimension = strategy.Dimension, buckets, total = buckets.Sum(b => b.Cost) });
});

// Unit economics: cost per token / inference / outcome (pass ?outcomes=N for the last).
app.MapGet("/v1/unit-economics", async (HttpRequest req, long? outcomes, IEventStore store, CancellationToken ct) =>
{
    var spans = await store.QueryAsync(new SpanQuery { TenantId = Tenant(req), Limit = int.MaxValue }, ct);
    return Results.Ok(new
    {
        costPerToken = new CostPerTokenMetric().Compute(spans, 0),
        costPerInference = new CostPerInferenceMetric().Compute(spans, 0),
        costPerOutcome = outcomes is { } n ? new CostPerOutcomeMetric().Compute(spans, n) : (decimal?)null,
        outcomes,
    });
});

// FOCUS-column cost rows (the cross-vendor billing schema; export-ready).
app.MapGet("/v1/focus", async (HttpRequest req, IEventStore store, CancellationToken ct) =>
{
    var spans = await store.QueryAsync(new SpanQuery { TenantId = Tenant(req), Limit = int.MaxValue }, ct);
    return Results.Ok(FocusProjection.Project(spans));
});

// Operational efficiency: latency, TTFT, cache-hit, error rate — derived from spans.
app.MapGet("/v1/efficiency", async (HttpRequest req, IEventStore store, CancellationToken ct) =>
{
    var spans = await store.QueryAsync(new SpanQuery { TenantId = Tenant(req), Limit = int.MaxValue }, ct);
    return Results.Ok(EfficiencyCalculator.Compute(spans));
});

// Score aggregation: attach an externally-computed eval score to a span/trace, and read them back.
app.MapPost("/v1/scores", async (HttpRequest req, ScoreDto dto, IScoreSink sink, TimeProvider clock, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(dto.TargetId) || string.IsNullOrWhiteSpace(dto.Name))
        return Results.BadRequest(new { error = "target_id and name are required" });
    await sink.AttachAsync(new Score
    {
        TenantId = Tenant(req), TargetId = dto.TargetId, Name = dto.Name,
        Numeric = dto.Numeric, Category = dto.Category, Boolean = dto.Boolean,
        Source = dto.Source, At = clock.GetUtcNow(),
    }, ct);
    return Results.Accepted(value: new { attached = true });
});

app.MapGet("/v1/spans/{spanId}/scores", async (HttpRequest req, string spanId, IScoreSink sink, CancellationToken ct) =>
    Results.Ok(await sink.GetForSpanAsync(Tenant(req), spanId, ct)));

app.Run();

// Wire DTO for POST /v1/scores (framework-agnostic externally-computed score).
public sealed record ScoreDto
{
    [System.Text.Json.Serialization.JsonPropertyName("target_id")] public string? TargetId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("numeric")] public double? Numeric { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("category")] public string? Category { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("boolean")] public bool? Boolean { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("source")] public string? Source { get; init; }
}

// Exposed so the test project can spin up the API via WebApplicationFactory.
public partial class Program;
