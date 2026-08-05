using UsageTracker.Contracts;
using UsageTracker.Cost;
using UsageTracker.Ingestion.Api;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Normalization;
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

app.Run();

// Exposed so the test project can spin up the API via WebApplicationFactory.
public partial class Program;
