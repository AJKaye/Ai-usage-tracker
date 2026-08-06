using System.Collections.Concurrent;
using UsageTracker.Contracts;
using UsageTracker.FinOps;

namespace UsageTracker.Ingestion.Api;

/// <summary>
/// Periodic FinOps evaluator (Phase 11) — mirrors <see cref="IngestConsumer"/>
/// (poison-isolated, cancellation-honoring) and reuses the <c>AdapterRunner</c>
/// de-dupe idea. On each tick it evaluates every tenant's budgets against actual
/// spend and scans its daily cost series for anomalies, raising in-app alerts (and
/// optionally a webhook). De-dupes so the same budget-state / anomaly-day doesn't
/// re-alert every tick. Interval + tenant discovery are injected/config-driven; the
/// scan is skipped entirely if no tenant has budgets (zero cost when unused).
/// </summary>
public sealed class BudgetScanService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly ILogger<BudgetScanService> _log;
    // de-dupe key -> last raised, so the same alert doesn't repeat each tick
    private readonly ConcurrentDictionary<string, bool> _raised = new();

    public BudgetScanService(IServiceProvider sp, TimeProvider clock, IConfiguration cfg,
        ILogger<BudgetScanService> log)
    {
        _sp = sp;
        _clock = clock;
        _log = log;
        var secs = cfg["BudgetScanIntervalSeconds"]
            ?? Environment.GetEnvironmentVariable("USAGETRACKER__BUDGET_SCAN_INTERVAL");
        _interval = int.TryParse(secs, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First scan shortly after startup, then every interval.
        using var timer = new PeriodicTimer(_interval);
        try
        {
            await ScanOnceAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ScanOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One full scan across all tenants with budgets. Exposed for tests.</summary>
    public async Task ScanOnceAsync(CancellationToken ct = default)
    {
        var store = _sp.GetRequiredService<IBudgetStore>();
        var events = _sp.GetRequiredService<IEventStore>();
        var alerts = _sp.GetRequiredService<IAlertSink>();
        var tenants = (store as InMemoryBudgetStore)?.TenantsWithBudgets() ?? Array.Empty<string>();
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        foreach (var tenant in tenants)
        {
            try { await ScanTenantAsync(tenant, today, store, events, alerts, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "budget scan failed for tenant {Tenant}", tenant);   // poison-isolated
            }
        }
    }

    private async Task ScanTenantAsync(string tenant, DateOnly today, IBudgetStore store,
        IEventStore events, IAlertSink alerts, CancellationToken ct)
    {
        // Budgets: evaluate each against its current period's spend.
        foreach (var budget in await store.ListAsync(tenant, ct))
        {
            var start = BudgetEvaluator.PeriodStart(budget.Period, today);
            var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var spans = await events.QueryAsync(new SpanQuery { TenantId = tenant, Since = since, Limit = int.MaxValue }, ct);
            var status = BudgetEvaluator.Evaluate(budget, spans, today);

            if (status.State is "warning" or "exceeded")
            {
                var kind = status.State == "exceeded" ? "budget_exceeded" : "budget_warning";
                // de-dupe per (budget, period-start, state) — re-alert only on state change / new period
                await RaiseOnce(alerts, tenant, kind, $"{budget.Id}:{start:o}:{status.State}",
                    $"Budget '{budget.Id}' {status.State}: {status.SpentToDate:0.####} of {budget.Limit:0.####} {budget.Currency} " +
                    $"({status.Utilization:P0}); projected {status.ProjectedEndOfPeriod:0.####}.",
                    status.SpentToDate, budget.Id, ct);
            }
        }

        // Anomalies: scan the trailing daily series for a spike on the latest day.
        var lookbackSince = new DateTimeOffset(today.AddDays(-14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var series = await events.SummarizeByDayAsync(new SpanQuery { TenantId = tenant, Since = lookbackSince }, ct);
        if (CostAnomalyDetector.Detect(series) is { } anomaly)
            await RaiseOnce(alerts, tenant, "cost_anomaly", $"anomaly:{anomaly.Day:o}",
                $"Cost anomaly on {anomaly.Day}: {anomaly.Cost:0.####} {anomaly.Currency} vs baseline " +
                $"{anomaly.BaselineMean:0.####} (z={anomaly.ZScore:0.0}).",
                anomaly.Cost, anomaly.Day.ToString("o"), ct);
    }

    private async Task RaiseOnce(IAlertSink alerts, string tenant, string kind, string dedupeKey,
        string message, decimal value, string reference, CancellationToken ct)
    {
        if (!_raised.TryAdd($"{tenant}|{dedupeKey}", true)) return;   // already alerted
        var alert = new Alert
        {
            Id = Guid.NewGuid().ToString("n"), TenantId = tenant, Kind = kind,
            Message = message, Value = value, At = _clock.GetUtcNow(), Reference = reference,
        };
        await alerts.RaiseAsync(alert, ct);
        // Optional outbound webhook — present only if USAGETRACKER__ALERT_WEBHOOK is set.
        // Itself egress-gated, so it fails closed under air-gap regardless.
        var notifier = _sp.GetService<INotifier>();
        if (notifier is not null)
        {
            try { await notifier.NotifyAsync(alert, ct); }
            catch (Exception ex) { _log.LogWarning("alert webhook failed: {Msg}", ex.Message); }
        }
    }
}
