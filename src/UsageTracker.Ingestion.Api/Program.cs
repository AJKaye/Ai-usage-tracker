using UsageTracker.Contracts;
using UsageTracker.Cost;
using UsageTracker.Ingestion.Api;
using UsageTracker.FinOps;
using UsageTracker.Ingestion.Otlp;
using UsageTracker.Mcp;
using UsageTracker.Normalization;
using UsageTracker.Portability;
using UsageTracker.Reconciliation;
using UsageTracker.Security;
using UsageTracker.Storage.DuckDb;
using UsageTracker.Storage.InMemory;
using UsageTracker.Storage.Sqlite;

// Pin the content root to the EXE's directory (not the launch CWD) so the embedded
// wwwroot + GOVERNANCE.md resolve wherever the .exe is double-clicked from — the
// Progressive Deployment ★ promise ("download, run anywhere"). Single-file publish
// extracts here too.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// --- DEPLOYMENT PROFILE (the "runs anywhere" knob) -------------------------
// USAGETRACKER__PROFILE selects the backend tier. Default is "solo": an embedded
// SQLite store that needs ZERO infrastructure — the mode the downloadable .exe
// runs in. "standard"/"distributed" swap in server backends (Postgres / ClickHouse
// + Kafka) behind the same IEventStore contract, so it's a config change only.
var profile = (Environment.GetEnvironmentVariable("USAGETRACKER__PROFILE")
    ?? builder.Configuration["Profile"] ?? "solo").Trim().ToLowerInvariant();

// --- Desktop window (solo): make the .exe feel like a native app -------------
// In the solo profile with no operator-pinned URL, bind a fixed loopback URL and
// open a chromeless browser window at it after startup. Servers/SaaS (pinned URL /
// other profile) and the test host are untouched. Best-effort; never fatal.
var pinnedUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var openWindow = DesktopWindow.ShouldOpen(profile, pinnedUrls,
    Environment.GetEnvironmentVariable("USAGETRACKER__NO_WINDOW"));
string desktopUrl = "http://127.0.0.1:5000";
if (openWindow)
    builder.WebHost.UseUrls(desktopUrl);   // deterministic port so we know where to open the window

// --- composition root: wire the replaceable modules behind their contracts ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(TokenNormalizerRegistry.CreateDefault());

// Air-gap egress policy (D6): solo/ephemeral fail-closed on any outbound call.
var egress = EgressPolicy.ForProfile(profile);
builder.Services.AddSingleton(egress);
builder.Services.AddSingleton<IEgressGuard>(egress);   // the contract other modules depend on

// Pricing catalog source. Review finding #1: if a signing public key + bundle are
// configured (USAGETRACKER__PRICING_BUNDLE / __PRICING_BUNDLE_SIG / __PRICING_PUBKEY),
// the bundle's ECDSA signature is VERIFIED before load and a tampered bundle is
// refused (D6/FedRAMP integrity). Absent that config, the unsigned dev seed is used
// and clearly logged as dev-only.
builder.Services.AddSingleton<IPriceCatalogSource>(sp =>
{
    var bundlePath = Environment.GetEnvironmentVariable("USAGETRACKER__PRICING_BUNDLE");
    var sigPath = Environment.GetEnvironmentVariable("USAGETRACKER__PRICING_BUNDLE_SIG");
    var pubKeyPath = Environment.GetEnvironmentVariable("USAGETRACKER__PRICING_PUBKEY");
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("pricing");
    if (bundlePath is not null && sigPath is not null && pubKeyPath is not null)
    {
        var verifier = EcdsaBundleVerifier.FromSpki(File.ReadAllBytes(pubKeyPath));
        var source = new SignedOfflineBundleSource(
            File.ReadAllBytes(bundlePath), File.ReadAllBytes(sigPath), verifier);
        log.LogInformation("pricing catalog: signed offline bundle verified (digest {Digest}).", source.Digest);
        return source;
    }
    log.LogWarning("pricing catalog: using the UNSIGNED dev seed — configure USAGETRACKER__PRICING_BUNDLE/_SIG/_PUBKEY for a signature-verified bundle (D6/air-gap).");
    return OfflineBundleCatalogSource.Seed();
});
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

// --- Phase 9: MCP server face (agents read their own spend via MCP) ----------
// Thin adapter over the SAME IEventStore query surface — one more transport, not a
// parallel data path. Exposed at POST /mcp as JSON-RPC 2.0.
builder.Services.AddSingleton<IMcpUsageProvider>(sp => new McpUsageProvider(sp.GetRequiredService<IEventStore>()));
builder.Services.AddSingleton(sp => new McpServer(sp.GetRequiredService<IMcpUsageProvider>()));

// --- Phase 10: backup/restore + data portability (also the migration substrate) ---
builder.Services.AddSingleton(sp => new DataPortabilityService(
    sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<TimeProvider>()));

// --- Phase 11: FinOps control plane (budgets / alerts / anomaly / forecast) --
builder.Services.AddSingleton<IBudgetStore, InMemoryBudgetStore>();
builder.Services.AddSingleton<IAlertSink, InMemoryAlertSink>();
// Optional outbound alert webhook — OFF unless USAGETRACKER__ALERT_WEBHOOK is set.
// Gated by IEgressGuard, so it fails closed in solo/air-gap regardless.
var alertWebhook = builder.Configuration["AlertWebhook"]
    ?? Environment.GetEnvironmentVariable("USAGETRACKER__ALERT_WEBHOOK");
if (!string.IsNullOrWhiteSpace(alertWebhook))
    builder.Services.AddSingleton<INotifier>(sp => new WebhookNotifier(
        new HttpClient(), new Uri(alertWebhook), sp.GetRequiredService<IEgressGuard>()));
// Background evaluator: scans budgets + anomalies per tenant on an interval.
builder.Services.AddSingleton<BudgetScanService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BudgetScanService>());

// --- Phase 7: security platform services -------------------------------------
// Tamper-evident audit sink (SOC 2 evidence) — always on.
builder.Services.AddSingleton<HashChainAuditSink>();
builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<HashChainAuditSink>());
builder.Services.AddSingleton<ITenantResolver, PrincipalTenantResolver>();
// Subject key vault for opt-in content sealing + crypto-shredding (right-to-delete).
builder.Services.AddSingleton<SubjectKeyVault>();
// Data-lifecycle policy: content capture is OPT-IN, off by default (PII posture).
// Enable per deployment via USAGETRACKER__CONTENT_CAPTURE=1 / config ContentCapture=true.
builder.Services.AddSingleton(_ =>
{
    var enabled = string.Equals(builder.Configuration["ContentCapture"]
        ?? Environment.GetEnvironmentVariable("USAGETRACKER__CONTENT_CAPTURE"), "1", StringComparison.Ordinal)
        || string.Equals(builder.Configuration["ContentCapture"], "true", StringComparison.OrdinalIgnoreCase);
    return new DataLifecyclePolicy
    {
        TenantId = "*", ResidencyRegion = "local",
        ContentCaptureEnabled = enabled,
    };
});
builder.Services.AddSingleton<ContentVaultService>();
// Identity provider: API keys resolved from config (USAGETRACKER__APIKEYS = "key:tenant:role,...").
// Absent config → no keys registered → the dev header path (below) is used.
builder.Services.AddSingleton<IIdentityProvider>(_ =>
{
    // Read from configuration first (test/host-scoped, no process-global leakage),
    // then the env var as a deployment convenience.
    var spec = builder.Configuration["ApiKeys"]
        ?? Environment.GetEnvironmentVariable("USAGETRACKER__APIKEYS");
    var bindings = new Dictionary<string, ApiKeyBinding>();
    if (!string.IsNullOrWhiteSpace(spec))
        foreach (var entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length >= 2)
                bindings[parts[0]] = new ApiKeyBinding
                {
                    TenantId = parts[1],
                    SubjectId = parts.Length > 3 ? parts[3] : $"key:{parts[1]}",
                    Roles = parts.Length > 2 ? new[] { parts[2] } : Array.Empty<string>(),
                };
        }
    return new ApiKeyIdentityProvider(bindings);
});

// The ONLY line that changes per deployment tier — every profile satisfies IEventStore.
builder.Services.AddSingleton<IEventStore>(_ => profile switch
{
    // Embedded, durable, zero-infra — the .exe default. DB path is configurable;
    // defaults to a file next to the executable so a plain double-click just works.
    "solo" or "embedded" => SqliteEventStore.ForFile(
        Environment.GetEnvironmentVariable("USAGETRACKER__DB")
        ?? Path.Combine(AppContext.BaseDirectory, "usage-tracker.db")),

    // Embedded COLUMNAR OLAP — still zero-infra (DuckDB native lib ships in the .exe),
    // but fast SUM/GROUP BY over large span sets. The self-contained analytics tier;
    // pick this over 'solo' when a single node handles high analytics volume.
    "analytics" or "duckdb" => DuckDbEventStore.ForFile(
        Environment.GetEnvironmentVariable("USAGETRACKER__DB")
        ?? Path.Combine(AppContext.BaseDirectory, "usage-tracker.duckdb")),

    // Volatile — for tests / throwaway runs.
    "ephemeral" or "test" => new InMemoryEventStore(),

    // Server tiers = MULTI-NODE HA / horizontal scale-out ONLY (ADR-0010). They are
    // NOT a capability the self-contained build lacks — 'analytics' (DuckDB) gives
    // columnar OLAP in-process. Reach for these only when one node genuinely can't
    // keep up or a hard availability SLA demands failover. Infra-blocked here (need
    // a cluster); fail fast rather than silently mis-store.
    "standard" => throw new NotSupportedException(
        "profile 'standard' (Postgres, multi-node) needs server infra — use 'solo'/'analytics' for the zero-infra build (self-contained, incl. columnar OLAP via 'analytics')."),
    "distributed" => throw new NotSupportedException(
        "profile 'distributed' (ClickHouse+Kafka+Postgres) is the multi-node HA scale tier and needs a cluster (Docker/K8s) — use 'analytics' for self-contained columnar OLAP on a single node (ADR-0010)."),

    _ => throw new ArgumentException($"unknown USAGETRACKER__PROFILE '{profile}' (expected solo|analytics|ephemeral|standard|distributed)"),
});

builder.Services.AddSingleton<IngestionService>();

// --- Phase 2: OTLP ingestion — mapper registry + async hot-path channel -----
// Span mapping (wire → canonical) runs synchronously on the request (cheap,
// pure); cost + persist run on the background consumer draining the channel.
builder.Services.AddSingleton(sp =>
    SpanMapperRegistry.CreateDefault(sp.GetRequiredService<TokenNormalizerRegistry>()));
builder.Services.AddSingleton<ChannelIngest>();
builder.Services.AddSingleton<IIngestChannel>(sp => sp.GetRequiredService<ChannelIngest>());
// Register the consumer as a shared singleton AND as the hosted service, so the
// self-observability endpoint reads the SAME instance's counters.
builder.Services.AddSingleton<IngestConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestConsumer>());

var app = builder.Build();

// Process start (self-observability uptime). Set here — Date.Now at composition time.
var startedAt = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();

app.Logger.LogInformation("AI Usage Tracker starting — profile='{Profile}'", profile);

// --- Phase 8: serve the embedded SPA (the exe ships WITH its UI) -------------
// Static assets + index.html live in wwwroot (the built React bundle, copied at
// publish). These are public (the SPA authenticates its own API calls), so static
// serving runs BEFORE the auth gate.
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Phase 7: auth + tenant-scoping middleware -------------------------------
// Runs once per request: /health + the SPA/static + governance read are open;
// every /v1 data call authenticates (if a credential is presented) and resolves the
// tenant from the verified principal. Invalid credential → 401. Keyless dev → header.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    // Open paths: health, the governance matrix (public compliance disclosure), and
    // anything that isn't a data API call (SPA routes + static assets). /v1 + /mcp
    // are tenant-scoped data paths and go through the auth gate.
    bool isDataPath = path.StartsWithSegments("/v1") || path.StartsWithSegments("/mcp");
    if (path.StartsWithSegments("/health")
        || path.StartsWithSegments("/v1/governance")
        || path.StartsWithSegments("/v1/platform")
        || !isDataPath)
    {
        await next();
        return;
    }

    var idp = ctx.RequestServices.GetRequiredService<IIdentityProvider>();
    var resolver = ctx.RequestServices.GetRequiredService<ITenantResolver>();
    var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    var principal = await idp.AuthenticateAsync(headers);
    if (principal is not null)
    {
        ctx.Items["tenant"] = resolver.Resolve(headers, principal);
        ctx.Items["principal"] = principal;
    }
    else if (ApiKeyIdentityProvider.ExtractCredential(headers) is not null)
    {
        // A credential was presented but did not authenticate → fail closed.
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid credential" });
        return;
    }
    else
    {
        // Keyless dev posture: header-scoped (or "default").
        ctx.Items["tenant"] = ctx.Request.Headers.TryGetValue("X-Tenant-Id", out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToString() : "default";
    }
    await next();
});

// --- Phase 7 (wired): audit every mutating data request ----------------------
// Runs after auth (so tenant + principal are resolved). Records a tamper-evident
// AuditEvent for POST/PUT/DELETE on /v1 + /mcp — turning the hash-chain sink from a
// tested library into enforced product behavior (SOC 2 evidence on every write).
app.Use(async (ctx, next) =>
{
    await next();   // let the request run first, so we record its real outcome
    var m = ctx.Request.Method;
    var path = ctx.Request.Path.Value ?? "";
    bool mutating = (HttpMethods.IsPost(m) || HttpMethods.IsPut(m) || HttpMethods.IsDelete(m))
                    && (path.StartsWith("/v1") || path.StartsWith("/mcp"));
    if (!mutating) return;
    var sink = ctx.RequestServices.GetRequiredService<IAuditSink>();
    var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
    var tenant = ctx.Items.TryGetValue("tenant", out var t) && t is string s ? s : "default";
    await sink.RecordAsync(new AuditEvent
    {
        TenantId = tenant,
        Actor = Actor(ctx),
        Action = $"{m} {path}",
        At = clock.GetUtcNow(),
        Success = ctx.Response.StatusCode is >= 200 and < 400,
    });
});

// --- tenant resolution (Phase 7) ---
// The auth MIDDLEWARE (below) resolves the tenant once per request and stashes it in
// HttpContext.Items; endpoints read it synchronously here. If API keys are configured
// a request MUST authenticate and its tenant comes from the verified Principal (a
// client cannot assert an arbitrary tenant); keyless dev falls back to X-Tenant-Id.
static string Tenant(HttpRequest r) =>
    r.HttpContext.Items.TryGetValue("tenant", out var t) && t is string s ? s : "default";

// The authenticated subject for audit attribution, or "anonymous" in keyless dev.
static string Actor(HttpContext ctx) =>
    ctx.Items.TryGetValue("principal", out var p) && p is Principal pr ? pr.SubjectId : "anonymous";

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ingestion-api", version = "0.1.0" }));

// --- Phase 10: platform self-observability (dogfood; public ops signal) -------
// Uptime + ingest throughput + queue depth + backend tier — the on-call/SLO signal.
app.MapGet("/v1/platform/stats", (ChannelIngest channel, IngestConsumer consumer, IEventStore store, TimeProvider clock) =>
    Results.Ok(new
    {
        service = "ai-usage-tracker",
        version = "0.1.0",
        profile,
        store = store.GetType().Name,
        uptimeSeconds = (long)(clock.GetUtcNow() - startedAt).TotalSeconds,
        ingest = new
        {
            enqueued = channel.Enqueued,
            processed = consumer.Processed,
            failed = consumer.Failed,
            queueDepth = channel.QueueDepth,
        },
    }));

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

// --- Phase 11: FinOps control plane API (budgets / status / anomalies / forecast / alerts) ---
// Turns the passive views above into an active control plane. All tenant-scoped; the
// mutating routes are audited for free by the middleware. Evaluation reuses the pure
// BudgetEvaluator/CostAnomalyDetector/SpendForecaster over the tenant's spans.

// CRUD budgets. POST upserts (client-supplied id → update; else server assigns one).
app.MapGet("/v1/budgets", async (HttpRequest req, IBudgetStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(Tenant(req), ct)));

app.MapPost("/v1/budgets", async (HttpRequest req, BudgetDto dto, IBudgetStore store, CancellationToken ct) =>
{
    if (dto.Limit is not > 0)
        return Results.BadRequest(new { error = "limit must be greater than zero" });
    var period = string.Equals(dto.Period, "daily", StringComparison.OrdinalIgnoreCase)
        ? BudgetPeriod.Daily : BudgetPeriod.Monthly;
    var budget = new Budget
    {
        Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("n") : dto.Id!,
        TenantId = Tenant(req),
        Dimension = dto.Dimension ?? "",
        DimensionValue = string.IsNullOrWhiteSpace(dto.DimensionValue) ? null : dto.DimensionValue,
        Limit = dto.Limit!.Value,
        Currency = string.IsNullOrWhiteSpace(dto.Currency) ? "USD" : dto.Currency!,
        Period = period,
        WarnAtFraction = dto.WarnAtFraction is > 0 and < 1 ? dto.WarnAtFraction!.Value : 0.8,
    };
    await store.UpsertAsync(budget, ct);
    return Results.Ok(budget);
});

app.MapDelete("/v1/budgets/{budgetId}", async (HttpRequest req, string budgetId, IBudgetStore store, CancellationToken ct) =>
{
    await store.DeleteAsync(Tenant(req), budgetId, ct);
    return Results.Ok(new { deleted = budgetId });
});

// Live evaluation: each budget vs its current-period spend → spent/utilization/state/projection.
app.MapGet("/v1/budgets/status", async (HttpRequest req, IBudgetStore budgets, IEventStore store, TimeProvider clock, CancellationToken ct) =>
{
    var tenant = Tenant(req);
    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var defs = await budgets.ListAsync(tenant, ct);
    var statuses = new List<BudgetStatus>(defs.Count);
    foreach (var b in defs)
    {
        var start = BudgetEvaluator.PeriodStart(b.Period, today);
        var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var spans = await store.QueryAsync(new SpanQuery { TenantId = tenant, Since = since, Limit = int.MaxValue }, ct);
        statuses.Add(BudgetEvaluator.Evaluate(b, spans, today));
    }
    return Results.Ok(statuses);
});

// Cost anomalies over the tenant's trailing daily series (?baselineDays, ?k, ?lookbackDays).
app.MapGet("/v1/anomalies", async (HttpRequest req, int? baselineDays, double? k, int? lookbackDays,
        IEventStore store, TimeProvider clock, CancellationToken ct) =>
{
    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var look = lookbackDays is > 0 ? lookbackDays.Value : 30;
    var since = new DateTimeOffset(today.AddDays(-look).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    var series = await store.SummarizeByDayAsync(new SpanQuery { TenantId = Tenant(req), Since = since }, ct);
    var anomaly = CostAnomalyDetector.Detect(series, baselineDays is > 0 ? baselineDays.Value : 7, k is > 0 ? k.Value : 3.0);
    return Results.Ok(new { anomaly, series });
});

// Month-to-date spend + run-rate projection for the current calendar month.
app.MapGet("/v1/forecast", async (HttpRequest req, IEventStore store, TimeProvider clock, CancellationToken ct) =>
{
    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    var monthStart = new DateTimeOffset(new DateOnly(today.Year, today.Month, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    var mtd = await store.SummarizeByDayAsync(new SpanQuery { TenantId = Tenant(req), Since = monthStart }, ct);
    var spent = mtd.Sum(d => d.Cost);
    var projected = SpendForecaster.ForecastMonth(mtd, today);
    return Results.Ok(new { month = $"{today.Year:0000}-{today.Month:00}", spentToDate = spent, projectedEndOfMonth = projected, series = mtd });
});

// In-app alert feed (works in every profile, incl. air-gapped). ?limit caps the recent window.
app.MapGet("/v1/alerts", async (HttpRequest req, int? limit, IAlertSink sink, CancellationToken ct) =>
    Results.Ok(await sink.RecentAsync(Tenant(req), limit is > 0 ? limit.Value : 100, ct)));

// --- Phase 8: Regulatory Governance matrix (backs the in-product governance page) ---
// Parsed live from GOVERNANCE.md so the page never drifts from the maintained source.
// Public (compliance disclosure). GOVERNANCE.md is copied next to the exe at publish;
// falls back to the content root / repo root in dev.
app.MapGet("/v1/governance", (IWebHostEnvironment env) =>
{
    string[] candidates =
    {
        Path.Combine(AppContext.BaseDirectory, "GOVERNANCE.md"),
        Path.Combine(env.ContentRootPath, "GOVERNANCE.md"),
        Path.Combine(env.ContentRootPath, "..", "..", "GOVERNANCE.md"),
    };
    var path = candidates.FirstOrDefault(File.Exists);
    if (path is null)
        return Results.Ok(new GovernanceMatrix
        {
            Controls = Array.Empty<GovernanceControl>(),
            LastUpdated = "unavailable",
            StatusCounts = new Dictionary<string, int>(),
        });
    return Results.Ok(GovernanceParser.Parse(File.ReadAllText(path)));
});

// --- Phase 7 (wired): tamper-evident audit export (SOC 2 evidence) ----------
// The tenant's append-only audit chain + an integrity check. Tenant-scoped.
app.MapGet("/v1/audit", (HttpRequest req, HashChainAuditSink audit) =>
{
    var tenant = Tenant(req);
    return Results.Ok(new { tenant, intact = audit.Verify(tenant), entries = audit.Export(tenant) });
});

// --- Phase 7 (wired): opt-in content capture + crypto-shred right-to-delete --
// Content is PII: capture is OFF by default and 409s unless the tenant opts in.
// When on, prompt/response text is sealed under the subject's per-subject key.
app.MapPost("/v1/content", (HttpRequest req, ContentDto dto, ContentVaultService vault) =>
{
    if (string.IsNullOrWhiteSpace(dto.SpanId) || string.IsNullOrWhiteSpace(dto.SubjectId) || dto.Content is null)
        return Results.BadRequest(new { error = "span_id, subject_id and content are required" });
    if (!vault.CaptureEnabled)
        return Results.Conflict(new { error = "content capture is disabled (opt-in; set USAGETRACKER__CONTENT_CAPTURE=1)" });
    vault.Capture(Tenant(req), dto.SpanId, dto.SubjectId, dto.Content);
    return Results.Accepted(value: new { sealed_ = true, spanId = dto.SpanId });
});

app.MapGet("/v1/content/{spanId}", (HttpRequest req, string spanId, ContentVaultService vault) =>
{
    var text = vault.Reveal(Tenant(req), spanId);
    return text is null ? Results.NotFound() : Results.Ok(new { spanId, content = text });
});

// GDPR/HIPAA right-to-delete: crypto-shred a subject → their content is permanently
// unrecoverable; aggregates (tokens/cost) persist.
app.MapDelete("/v1/subjects/{subjectId}", (string subjectId, ContentVaultService vault) =>
    Results.Ok(new { subjectId, erased = vault.EraseSubject(subjectId) }));

// --- Phase 9: MCP server face (JSON-RPC 2.0) --------------------------------
// An MCP client POSTs JSON-RPC here to read its own spend live (tools/resources).
// Tenant-scoped like every data path; the server never trusts a client tenant.
app.MapPost("/mcp", async (HttpRequest req, McpServer mcp, CancellationToken ct) =>
{
    using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
    var response = await mcp.HandleAsync(doc.RootElement, Tenant(req), ct);
    return response is null ? Results.NoContent() : Results.Ok(response);   // null = JSON-RPC notification
});

// --- Phase 10: data export / import (backup-restore + solo→distributed migration) ---
// GET /v1/export → a portable JSON bundle of the tenant's data (download it as backup,
// or feed it to another store to migrate). POST /v1/import restores/loads a bundle
// (idempotent by (tenant, span)). Tenant-scoped like every data path.
app.MapGet("/v1/export", async (HttpRequest req, DataPortabilityService port, CancellationToken ct) =>
{
    var json = await port.ExportJsonAsync(Tenant(req), ct);
    return Results.Text(json, "application/json");
});

app.MapPost("/v1/import", async (HttpRequest req, DataPortabilityService port, CancellationToken ct) =>
{
    using var reader = new StreamReader(req.Body);
    var json = await reader.ReadToEndAsync(ct);
    if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "empty bundle" });
    try
    {
        return Results.Ok(await port.ImportJsonAsync(Tenant(req), json, ct));
    }
    catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// SPA fallback: any non-API, non-file route serves index.html so client-side routing
// works (deep links to /dashboard, /governance, …). No-op if no SPA bundle is present.
app.MapFallbackToFile("index.html");

// Solo desktop: once the server is listening, open the SPA in an app-mode window.
if (openWindow)
    app.Lifetime.ApplicationStarted.Register(() =>
        DesktopWindow.Open(desktopUrl, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("desktop")));

app.Run();

// Wire DTO for POST /v1/content (opt-in captured content, sealed per-subject).
public sealed record ContentDto
{
    [System.Text.Json.Serialization.JsonPropertyName("span_id")] public string? SpanId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("subject_id")] public string? SubjectId { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("content")] public string? Content { get; init; }
}

// Wire DTO for POST /v1/budgets (a spend limit; snake_case JSON like the other DTOs).
public sealed record BudgetDto
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("dimension")] public string? Dimension { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("dimension_value")] public string? DimensionValue { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("limit")] public decimal? Limit { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("currency")] public string? Currency { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("period")] public string? Period { get; init; }   // "daily" | "monthly"
    [System.Text.Json.Serialization.JsonPropertyName("warn_at_fraction")] public double? WarnAtFraction { get; init; }
}

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
