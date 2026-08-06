using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageTracker.Contracts;

namespace UsageTracker.Storage.Sqlite;

/// <summary>
/// Durable, embedded <see cref="IEventStore"/> backed by SQLite — the storage for
/// the zero-infra downloadable .exe (solo profile). No server, no Docker: the
/// native engine ships in the package and, under self-contained publish, inside
/// the single executable. Satisfies the identical contract + conformance suite as
/// the ClickHouse store, so "solo" and "distributed" are the same product.
///
/// Tenancy: every read/write is filtered by tenant_id in SQL — the embedded-tier
/// equivalent of the Postgres RLS the distributed tier uses. A caller for tenant
/// A can never read tenant B's rows.
///
/// Spans are stored as scalar columns for the query/filter path + JSON blobs for
/// the rich usage/cost objects (avoids a wide, migration-heavy table while the
/// schema is still moving; the ClickHouse store uses typed columns for analytics).
/// </summary>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly string _connString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A shared keep-alive connection so an in-memory DB (":memory:") survives
    // between calls; file-backed DBs use it too, harmlessly.
    private readonly SqliteConnection _keepAlive;

    public SqliteEventStore(string connectionString)
    {
        _connString = connectionString;
        _keepAlive = new SqliteConnection(_connString);
        _keepAlive.Open();
        Initialize();
    }

    /// <summary>File-backed store at <paramref name="path"/> (the .exe default).</summary>
    public static SqliteEventStore ForFile(string path)
        => new($"Data Source={path};Cache=Shared");

    /// <summary>Shared in-memory store (tests) — one name, kept alive by the connection.</summary>
    public static SqliteEventStore InMemoryShared(string name = "ut")
        => new($"Data Source={name};Mode=Memory;Cache=Shared");

    private void Initialize()
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS spans (
                tenant_id       TEXT NOT NULL,
                span_id         TEXT NOT NULL,
                trace_id        TEXT NOT NULL,
                parent_span_id  TEXT,
                session_id      TEXT,
                kind            INTEGER NOT NULL,
                name            TEXT,
                status          TEXT,
                provider        TEXT,
                request_model   TEXT,
                response_model  TEXT,
                granularity     INTEGER NOT NULL,
                start_time      TEXT NOT NULL,       -- ISO-8601, sortable
                input_tokens    INTEGER,
                output_tokens   INTEGER,
                total_cost      TEXT,                -- decimal as invariant string
                currency        TEXT,
                usage_json      TEXT,                -- NormalizedUsage
                cost_json       TEXT,                -- CostBreakdown
                span_json       TEXT NOT NULL,       -- full Span (source of truth for Get)
                PRIMARY KEY (tenant_id, span_id)      -- idempotent by (tenant, span)
            );
            CREATE INDEX IF NOT EXISTS ix_spans_tenant_start ON spans(tenant_id, start_time DESC);
            CREATE INDEX IF NOT EXISTS ix_spans_tenant_provider ON spans(tenant_id, provider);
            CREATE INDEX IF NOT EXISTS ix_spans_tenant_trace ON spans(tenant_id, trace_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    public Task AppendAsync(Span span, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // INSERT OR REPLACE = idempotent by (tenant_id, span_id) — dedup on resend.
        cmd.CommandText = """
            INSERT OR REPLACE INTO spans
              (tenant_id, span_id, trace_id, parent_span_id, session_id, kind, name, status,
               provider, request_model, response_model, granularity, start_time,
               input_tokens, output_tokens, total_cost, currency, usage_json, cost_json, span_json)
            VALUES
              ($tenant,$span,$trace,$parent,$session,$kind,$name,$status,
               $provider,$reqmodel,$respmodel,$gran,$start,
               $intok,$outtok,$cost,$currency,$usage,$costj,$spanj);
            """;
        void P(string n, object? v) => cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        P("$tenant", span.TenantId);
        P("$span", span.SpanId);
        P("$trace", span.TraceId);
        P("$parent", span.ParentSpanId);
        P("$session", span.SessionId);
        P("$kind", (int)span.Kind);
        P("$name", span.Name);
        P("$status", span.Status);
        P("$provider", span.Provider);
        P("$reqmodel", span.RequestModel);
        P("$respmodel", span.ResponseModel);
        P("$gran", (int)span.Granularity);
        P("$start", span.StartTime.ToString("O"));
        P("$intok", span.Usage?.InputTokens);
        P("$outtok", span.Usage?.OutputTokens);
        P("$cost", span.EstimatedCost?.TotalCost.ToString(System.Globalization.CultureInfo.InvariantCulture));
        P("$currency", span.EstimatedCost?.Currency);
        P("$usage", span.Usage is null ? null : JsonSerializer.Serialize(span.Usage, Json));
        P("$costj", span.EstimatedCost is null ? null : JsonSerializer.Serialize(span.EstimatedCost, Json));
        P("$spanj", JsonSerializer.Serialize(span, Json));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<Span?> GetAsync(string tenantId, string spanId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT span_json FROM spans WHERE tenant_id=$t AND span_id=$s LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", tenantId);
        cmd.Parameters.AddWithValue("$s", spanId);
        var json = cmd.ExecuteScalar() as string;
        return Task.FromResult(json is null ? null : JsonSerializer.Deserialize<Span>(json, Json));
    }

    public Task<IReadOnlyList<Span>> QueryAsync(SpanQuery q, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var sql = "SELECT span_json FROM spans WHERE tenant_id=$t";
        cmd.Parameters.AddWithValue("$t", q.TenantId);
        if (q.TraceId is { } tr) { sql += " AND trace_id=$tr"; cmd.Parameters.AddWithValue("$tr", tr); }
        if (q.Provider is { } pv) { sql += " AND provider=$pv COLLATE NOCASE"; cmd.Parameters.AddWithValue("$pv", pv); }
        if (q.Since is { } since) { sql += " AND start_time>=$since"; cmd.Parameters.AddWithValue("$since", since.ToString("O")); }
        if (q.Until is { } until) { sql += " AND start_time<$until"; cmd.Parameters.AddWithValue("$until", until.ToString("O")); }
        sql += " ORDER BY start_time DESC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$lim", q.Limit);
        cmd.CommandText = sql;

        var list = new List<Span>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var s = JsonSerializer.Deserialize<Span>(r.GetString(0), Json);
            if (s is not null) list.Add(s);
        }
        return Task.FromResult<IReadOnlyList<Span>>(list);
    }

    public async Task<UsageSummary> SummarizeAsync(SpanQuery q, CancellationToken ct = default)
    {
        // Reuse the filtered query, then aggregate in-process. For the embedded
        // tier the row counts are modest; the distributed store aggregates in SQL.
        var spans = await QueryAsync(q with { Limit = int.MaxValue }, ct);
        long inTok = 0, outTok = 0;
        decimal total = 0m;
        string currency = "USD";
        var byProvider = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var byModel = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in spans)
        {
            if (s.Usage is { } u) { inTok += u.InputTokens; outTok += u.OutputTokens; }
            if (s.EstimatedCost is { } cost)
            {
                total += cost.TotalCost;
                currency = cost.Currency;
                if (s.Provider is { } p) byProvider[p] = byProvider.GetValueOrDefault(p) + cost.TotalCost;
                var model = s.ResponseModel ?? s.RequestModel;
                if (model is { } m) byModel[m] = byModel.GetValueOrDefault(m) + cost.TotalCost;
            }
        }

        return new UsageSummary
        {
            SpanCount = spans.Count,
            TotalInputTokens = inTok,
            TotalOutputTokens = outTok,
            TotalEstimatedCost = total,
            Currency = currency,
            CostByProvider = byProvider,
            CostByModel = byModel,
        };
    }

    // Per-day cost rollup (FinOps time series). start_time is ISO-8601, so the date is
    // the first 10 chars (YYYY-MM-DD) and groups lexically. total_cost is stored as an
    // invariant decimal string (exact money) — so we group in SQL and sum the decimals
    // in-process per day rather than let SQLite coerce TEXT→float.
    public Task<IReadOnlyList<DailyCost>> SummarizeByDayAsync(SpanQuery q, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var sql = "SELECT substr(start_time,1,10) AS d, total_cost, currency FROM spans WHERE tenant_id=$t AND total_cost IS NOT NULL";
        cmd.Parameters.AddWithValue("$t", q.TenantId);
        if (q.Since is { } since) { sql += " AND start_time>=$since"; cmd.Parameters.AddWithValue("$since", since.ToString("O")); }
        if (q.Until is { } until) { sql += " AND start_time<$until"; cmd.Parameters.AddWithValue("$until", until.ToString("O")); }
        cmd.CommandText = sql + ";";

        var cost = new Dictionary<DateOnly, decimal>();
        var count = new Dictionary<DateOnly, long>();
        string currency = "USD";
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var day = DateOnly.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture);
                cost[day] = cost.GetValueOrDefault(day)
                    + decimal.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
                count[day] = count.GetValueOrDefault(day) + 1;
                if (!r.IsDBNull(2)) currency = r.GetString(2);
            }

        IReadOnlyList<DailyCost> series = cost.Keys.OrderBy(d => d)
            .Select(d => new DailyCost(d, cost[d], count[d], currency)).ToList();
        return Task.FromResult(series);
    }

    public void Dispose() => _keepAlive.Dispose();
}
