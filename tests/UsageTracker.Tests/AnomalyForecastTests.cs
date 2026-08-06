using System.Net;
using System.Text;
using UsageTracker.Contracts;
using UsageTracker.FinOps;
using UsageTracker.Security;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 11 / FinOps control plane — anomaly detection (z-score vs trailing baseline),
/// run-rate forecast, and the egress-gated webhook notifier. Transparent + deterministic.
/// </summary>
public class AnomalyForecastTests
{
    private static DailyCost Day(int day, decimal cost) =>
        new(new DateOnly(2026, 8, day), cost, 1, "USD");

    [Fact]
    public void Spike_after_a_flat_baseline_is_flagged()
    {
        // 7 flat days at 10, then a spike to 100 → clear anomaly.
        var series = new List<DailyCost>();
        for (int d = 1; d <= 7; d++) series.Add(Day(d, 10m));
        series.Add(Day(8, 100m));

        var a = CostAnomalyDetector.Detect(series, baselineDays: 7, k: 3.0);
        Assert.NotNull(a);
        Assert.Equal(new DateOnly(2026, 8, 8), a!.Day);
        Assert.Equal(100m, a.Cost);
        Assert.Equal(10m, a.BaselineMean);
    }

    [Fact]
    public void Normal_day_within_baseline_is_not_flagged()
    {
        // Noisy-but-normal baseline; last day is in range.
        var series = new List<DailyCost>
        {
            Day(1, 10m), Day(2, 12m), Day(3, 9m), Day(4, 11m), Day(5, 10m), Day(6, 13m), Day(7, 11m),
        };
        Assert.Null(CostAnomalyDetector.Detect(series, baselineDays: 6, k: 3.0));
    }

    [Fact]
    public void Too_little_history_returns_null()
    {
        Assert.Null(CostAnomalyDetector.Detect(new[] { Day(1, 10m) }));
        Assert.Null(CostAnomalyDetector.Detect(Array.Empty<DailyCost>()));
    }

    [Fact]
    public void Forecast_projects_run_rate_to_month_end()
    {
        // spent 50 over 10 elapsed days → 5/day → 5×31 = 155 projected (Aug = 31 days).
        var mtd = new List<DailyCost>();
        for (int d = 1; d <= 10; d++) mtd.Add(Day(d, 5m));
        var projected = SpendForecaster.ForecastMonth(mtd, new DateOnly(2026, 8, 10));
        Assert.Equal(155m, projected);
    }

    // --- egress-gated webhook notifier ---
    private sealed class Tripwire : HttpMessageHandler
    {
        public bool Called { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Called = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    private static Alert SampleAlert() => new()
    {
        Id = "a1", TenantId = "t", Kind = "budget_exceeded", Message = "over", At = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Webhook_notifier_fails_closed_under_air_gap()
    {
        var tw = new Tripwire();
        var notifier = new WebhookNotifier(new HttpClient(tw), new Uri("https://hooks.invalid/x"),
            EgressPolicy.ForProfile("solo"));   // air-gapped
        await Assert.ThrowsAsync<AirGapViolationException>(() => notifier.NotifyAsync(SampleAlert()));
        Assert.False(tw.Called, "no outbound call under air-gap");
    }

    [Fact]
    public async Task Webhook_notifier_posts_when_egress_allowed()
    {
        var tw = new Tripwire();
        var notifier = new WebhookNotifier(new HttpClient(tw), new Uri("https://hooks.invalid/x"),
            EgressPolicy.ForProfile("distributed"));   // not air-gapped
        await notifier.NotifyAsync(SampleAlert());
        Assert.True(tw.Called);
    }
}
