using UsageTracker.Contracts;

namespace UsageTracker.FinOps;

/// <summary>
/// Transparent statistical cost-anomaly detection (ARCHITECTURE.md §6; owner choice:
/// explainable, deterministic, no ML/infra). Flags the most recent day whose spend
/// deviates from its trailing baseline by more than k standard deviations — a z-score
/// outlier. Pure over a daily cost series; the baseline is the days BEFORE the day
/// under test, so a spike is measured against normal behavior, not itself.
/// </summary>
public static class CostAnomalyDetector
{
    /// <param name="series">Ascending daily cost series (as from <c>IEventStore.SummarizeByDayAsync</c>).</param>
    /// <param name="baselineDays">How many trailing days form the baseline (default 7).</param>
    /// <param name="k">Std-dev multiplier for the upper bound (default 3 = ~99.7%).</param>
    /// <returns>The anomaly for the latest day if it exceeds mean + k·stddev, else null.</returns>
    public static AnomalyResult? Detect(IReadOnlyList<DailyCost> series, int baselineDays = 7, double k = 3.0)
    {
        if (series.Count < 2) return null;                       // need a baseline + a test day

        var test = series[^1];
        var baseline = series
            .Take(series.Count - 1)                              // everything before the test day
            .TakeLast(baselineDays)
            .Select(d => (double)d.Cost)
            .ToList();
        if (baseline.Count < 2) return null;                     // too little history to judge

        double mean = baseline.Average();
        double variance = baseline.Sum(x => (x - mean) * (x - mean)) / baseline.Count;   // population stddev
        double stddev = Math.Sqrt(variance);

        double upper = mean + k * stddev;
        // With zero variance (flat history), any strictly-greater day is anomalous.
        bool isAnomaly = stddev > 0 ? (double)test.Cost > upper : (double)test.Cost > mean;
        if (!isAnomaly) return null;

        // z is undefined against a perfectly flat baseline (zero variance) — report null,
        // not Infinity/NaN (which also can't be JSON-serialized), while still flagging the day.
        double? z = stddev > 0 ? ((double)test.Cost - mean) / stddev : null;
        return new AnomalyResult
        {
            Day = test.Day,
            Cost = test.Cost,
            BaselineMean = (decimal)mean,
            ExpectedUpperBound = (decimal)upper,
            ZScore = z,
            Currency = test.Currency,
        };
    }
}

/// <summary>
/// Transparent run-rate spend forecast (owner choice). Projects period-end spend as
/// spend-to-date + average-daily-run-rate × days-remaining — the FinOps-standard
/// linear projection. Pure over a month-to-date daily series.
/// </summary>
public static class SpendForecaster
{
    /// <param name="monthToDate">Ascending daily cost series for the current month, up to and incl. today.</param>
    /// <param name="today">Current date (UTC).</param>
    /// <returns>Projected total spend for the calendar month.</returns>
    public static decimal ForecastMonth(IReadOnlyList<DailyCost> monthToDate, DateOnly today)
    {
        decimal spent = monthToDate.Sum(d => d.Cost);
        int elapsed = today.Day;                                 // days elapsed incl. today
        int total = DateTime.DaysInMonth(today.Year, today.Month);
        if (elapsed <= 0) return spent;
        decimal perDay = spent / elapsed;
        return spent + perDay * (total - elapsed);
    }
}
