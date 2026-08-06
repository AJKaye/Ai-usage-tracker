using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using UsageTracker.Contracts;

namespace UsageTracker.Storage.DuckDb;

/// <summary>
/// Embedded COLUMNAR <see cref="IEventStore"/> backed by DuckDB — the self-contained
/// analytics tier. DuckDB is an in-process, vectorized OLAP engine: the
/// ClickHouse-class capability (fast SUM/GROUP BY over large span sets) with NO
/// server, NO Docker, NO admin — the native engine ships in the package and, under
/// self-contained publish, inside the single .exe.
///
/// Peer to <c>SqliteEventStore</c>: identical contract + the same
/// <c>EventStoreContractTests</c> conformance suite, tenant-scoped SQL (a caller for
/// tenant A can never read B), idempotent by (tenant, span). The difference is
/// <see cref="SummarizeAsync"/> aggregates IN-ENGINE (columnar SQL) rather than
/// pulling rows and rolling up in-process — that is the analytics win over SQLite.
/// </summary>
public sealed class DuckDbEventStore : IEventStore, IDisposable
{
    private readonly string _connString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A keep-alive connection so an in-memory DB survives between calls (as SQLite does).
    private readonly DuckDBConnection _keepAlive;
    private readonly object _gate = new();   // DuckDB connections are not thread-safe; serialize.

    public DuckDbEventStore(string connectionString)
    {
        _connString = connectionString;
        _keepAlive = new DuckDBConnection(_connString);
        _keepAlive.Open();
        Initialize();
    }

    /// <summary>File-backed store at <paramref name="path"/> (the analytics-profile default).</summary>
    public static DuckDbEventStore ForFile(string path) => new($"Data Source={path}");

    /// <summary>In-memory store (tests). DuckDB's in-memory DB lives with the connection.</summary>
    public static DuckDbEventStore InMemory() => new("Data Source=:memory:");

    private void Initialize()
    {
        using var cmd = _keepAlive.CreateCommand();
        // DuckDB DDL: typed columns for the analytics/filter path + a JSON text blob
        // as the source of truth for Get. DECIMAL(18,10) keeps money exact in-engine.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS spans (
                tenant_id       VARCHAR NOT NULL,
                span_id         VARCHAR NOT NULL,
                trace_id        VARCHAR NOT NULL,
                provider        VARCHAR,
                request_model   VARCHAR,
                response_model  VARCHAR,
                kind            INTEGER NOT NULL,
                granularity     INTEGER NOT NULL,
                start_time      TIMESTAMP NOT NULL,
                input_tokens    BIGINT,
                output_tokens   BIGINT,
                total_cost      DECIMAL(18,10),
                currency        VARCHAR,
                span_json       VARCHAR NOT NULL,
                PRIMARY KEY (tenant_id, span_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Task AppendAsync(Span span, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _keepAlive.CreateCommand();
            // DuckDB upsert (idempotent by the (tenant,span) PK) — no INSERT OR REPLACE.
            cmd.CommandText = """
                INSERT INTO spans
                  (tenant_id, span_id, trace_id, provider, request_model, response_model,
                   kind, granularity, start_time, input_tokens, output_tokens, total_cost, currency, span_json)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
                ON CONFLICT (tenant_id, span_id) DO UPDATE SET
                  trace_id=excluded.trace_id, provider=excluded.provider,
                  request_model=excluded.request_model, response_model=excluded.response_model,
                  kind=excluded.kind, granularity=excluded.granularity, start_time=excluded.start_time,
                  input_tokens=excluded.input_tokens, output_tokens=excluded.output_tokens,
                  total_cost=excluded.total_cost, currency=excluded.currency, span_json=excluded.span_json;
                """;
            void P(object? v) => cmd.Parameters.Add(new DuckDBParameter(v ?? DBNull.Value));
            P(span.TenantId);
            P(span.SpanId);
            P(span.TraceId);
            P(span.Provider);
            P(span.RequestModel);
            P(span.ResponseModel);
            P((int)span.Kind);
            P((int)span.Granularity);
            P(span.StartTime.UtcDateTime);                 // TIMESTAMP (UTC)
            P(span.Usage?.InputTokens);
            P(span.Usage?.OutputTokens);
            P(span.EstimatedCost?.TotalCost);              // DECIMAL — exact money
            P(span.EstimatedCost?.Currency);
            P(JsonSerializer.Serialize(span, Json));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    public Task<Span?> GetAsync(string tenantId, string spanId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _keepAlive.CreateCommand();
            cmd.CommandText = "SELECT span_json FROM spans WHERE tenant_id=$1 AND span_id=$2 LIMIT 1;";
            cmd.Parameters.Add(new DuckDBParameter(tenantId));
            cmd.Parameters.Add(new DuckDBParameter(spanId));
            var json = cmd.ExecuteScalar() as string;
            return Task.FromResult(json is null ? null : JsonSerializer.Deserialize<Span>(json, Json));
        }
    }

    public Task<IReadOnlyList<Span>> QueryAsync(SpanQuery q, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _keepAlive.CreateCommand();
            var sql = "SELECT span_json FROM spans WHERE tenant_id=$1";
            var ps = new List<object> { q.TenantId };
            if (q.TraceId is { } tr) { ps.Add(tr); sql += $" AND trace_id=${ps.Count}"; }
            if (q.Provider is { } pv) { ps.Add(pv); sql += $" AND lower(provider)=lower(${ps.Count})"; }
            if (q.Since is { } since) { ps.Add(since.UtcDateTime); sql += $" AND start_time>=${ps.Count}"; }
            if (q.Until is { } until) { ps.Add(until.UtcDateTime); sql += $" AND start_time<${ps.Count}"; }
            ps.Add(q.Limit); sql += $" ORDER BY start_time DESC LIMIT ${ps.Count};";
            cmd.CommandText = sql;
            foreach (var p in ps) cmd.Parameters.Add(new DuckDBParameter(p));

            var list = new List<Span>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var s = JsonSerializer.Deserialize<Span>(r.GetString(0), Json);
                if (s is not null) list.Add(s);
            }
            return Task.FromResult<IReadOnlyList<Span>>(list);
        }
    }

    public Task<UsageSummary> SummarizeAsync(SpanQuery q, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // COLUMNAR aggregation IN-ENGINE — the analytics win. Scalar totals first…
            (int count, long inTok, long outTok, decimal total, string currency) totals;
            using (var cmd = _keepAlive.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(*), COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0),
                           COALESCE(SUM(total_cost),0), COALESCE(MAX(currency),'USD')
                    FROM spans WHERE tenant_id=$1;
                    """;
                cmd.Parameters.Add(new DuckDBParameter(q.TenantId));
                using var r = cmd.ExecuteReader();
                r.Read();
                totals = (
                    (int)ToLong(r.GetValue(0)),
                    r.IsDBNull(1) ? 0 : ToLong(r.GetValue(1)),
                    r.IsDBNull(2) ? 0 : ToLong(r.GetValue(2)),
                    r.IsDBNull(3) ? 0m : ToDecimal(r.GetValue(3)),
                    r.GetString(4));
            }

            var byProvider = GroupSum(q.TenantId, "provider");
            var byModel = GroupSum(q.TenantId, "COALESCE(response_model, request_model)");

            return Task.FromResult(new UsageSummary
            {
                SpanCount = totals.count,
                TotalInputTokens = totals.inTok,
                TotalOutputTokens = totals.outTok,
                TotalEstimatedCost = totals.total,
                Currency = totals.currency,
                CostByProvider = byProvider,
                CostByModel = byModel,
            });
        }
    }

    // GROUP BY <expr> SUM(total_cost) in-engine; skips null keys.
    private Dictionary<string, decimal> GroupSum(string tenantId, string keyExpr)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = $"""
            SELECT {keyExpr} AS k, COALESCE(SUM(total_cost),0)
            FROM spans WHERE tenant_id=$1 AND {keyExpr} IS NOT NULL AND total_cost IS NOT NULL
            GROUP BY k;
            """;
        cmd.Parameters.Add(new DuckDBParameter(tenantId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            map[r.GetString(0)] = ToDecimal(r.GetValue(1));
        }
        return map;
    }

    // Per-day cost rollup — COLUMNAR in-engine (CAST date + SUM + GROUP BY), the
    // analytics-tier win over the default per-row bucketing. Honors Since/Until.
    public Task<IReadOnlyList<DailyCost>> SummarizeByDayAsync(SpanQuery q, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _keepAlive.CreateCommand();
            var sql = @"SELECT CAST(start_time AS DATE) AS d,
                               COALESCE(SUM(total_cost),0) AS c,
                               COUNT(*) AS n,
                               COALESCE(MAX(currency),'USD') AS cur
                        FROM spans WHERE tenant_id=$1 AND total_cost IS NOT NULL";
            var ps = new List<object> { q.TenantId };
            if (q.Since is { } since) { ps.Add(since.UtcDateTime); sql += $" AND start_time>=${ps.Count}"; }
            if (q.Until is { } until) { ps.Add(until.UtcDateTime); sql += $" AND start_time<${ps.Count}"; }
            sql += " GROUP BY d ORDER BY d;";
            cmd.CommandText = sql;
            foreach (var p in ps) cmd.Parameters.Add(new DuckDBParameter(p));

            var list = new List<DailyCost>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // DuckDB CAST(... AS DATE) surfaces as System.DateOnly; take it directly
                // (fall back to DateTime for any driver that returns a timestamp).
                var raw = r.GetValue(0);
                var day = raw is DateOnly d ? d
                    : DateOnly.FromDateTime(Convert.ToDateTime(raw, CultureInfo.InvariantCulture));
                list.Add(new DailyCost(day, ToDecimal(r.GetValue(1)), ToLong(r.GetValue(2)), r.GetString(3)));
            }
            return Task.FromResult<IReadOnlyList<DailyCost>>(list);
        }
    }

    // DuckDB returns integer SUM() as System.Numerics.BigInteger (and other numeric
    // widths besides Int64/Decimal), which Convert.To* can't cast directly. Normalize.
    private static long ToLong(object v) => v switch
    {
        long l => l,
        System.Numerics.BigInteger b => (long)b,
        int i => i,
        decimal d => (long)d,
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    private static decimal ToDecimal(object v) => v switch
    {
        decimal d => d,
        System.Numerics.BigInteger b => (decimal)b,
        long l => l,
        double db => (decimal)db,
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    public void Dispose() => _keepAlive.Dispose();
}
