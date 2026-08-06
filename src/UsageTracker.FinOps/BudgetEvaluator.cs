using UsageTracker.Contracts;

namespace UsageTracker.FinOps;

/// <summary>
/// Evaluates a <see cref="Budget"/> against actual spend for its current period —
/// the active-control-plane counterpart to the passive allocation views
/// (ARCHITECTURE.md §6). Pure + deterministic (spans + "today" in → status out), so
/// it is golden-testable and reusable by the API and the background scan alike.
///
/// Scope: reuses <c>DimensionAllocationStrategy.KeyFor</c> so a budget can target the
/// whole tenant ("" dimension) or one dimension value (team/model/provider/…), from
/// the SAME captured span dimensions allocation uses — no upstream tags required.
/// Projection: linear run-rate to period end (spent-to-date ÷ elapsed-days ×
/// total-days-in-period). State: exceeded ≥ 1.0, warning ≥ WarnAtFraction, else ok.
/// </summary>
public static class BudgetEvaluator
{
    /// <param name="periodSpans">Spans already filtered to the budget's current period window.</param>
    /// <param name="today">The current date (UTC) — the "as of" for spend-to-date and projection.</param>
    public static BudgetStatus Evaluate(Budget budget, IReadOnlyList<Span> periodSpans, DateOnly today)
    {
        // In-scope spans: whole tenant if no dimension, else those whose captured
        // dimension value matches (a null DimensionValue means "any value" = aggregate).
        var inScope = periodSpans.Where(s => InScope(budget, s)).ToList();
        decimal spent = CostPerTokenMetric.TotalCost(inScope);

        var (elapsed, total) = PeriodProgress(budget.Period, today);
        decimal projected = elapsed > 0 ? spent / elapsed * total : spent;

        double utilization = budget.Limit > 0 ? (double)(spent / budget.Limit) : 0;
        string state =
            spent >= budget.Limit ? "exceeded" :
            utilization >= budget.WarnAtFraction ? "warning" : "ok";

        return new BudgetStatus
        {
            Budget = budget,
            SpentToDate = spent,
            Limit = budget.Limit,
            Utilization = utilization,
            ProjectedEndOfPeriod = projected,
            State = state,
        };
    }

    private static bool InScope(Budget b, Span s)
    {
        if (string.IsNullOrEmpty(b.Dimension)) return true;            // whole-tenant budget
        var value = DimensionAllocationStrategy.KeyFor(s, b.Dimension);
        if (b.DimensionValue is null) return value is not null;         // aggregate across the dimension
        return string.Equals(value, b.DimensionValue, StringComparison.OrdinalIgnoreCase);
    }

    // Elapsed days (inclusive of today) and total days for the budget period.
    private static (int elapsed, int total) PeriodProgress(BudgetPeriod period, DateOnly today) => period switch
    {
        BudgetPeriod.Daily => (1, 1),
        BudgetPeriod.Monthly => (today.Day, DateTime.DaysInMonth(today.Year, today.Month)),
        _ => (1, 1),
    };

    /// <summary>The inclusive start of the budget's current period (for windowed span queries).</summary>
    public static DateOnly PeriodStart(BudgetPeriod period, DateOnly today) => period switch
    {
        BudgetPeriod.Daily => today,
        BudgetPeriod.Monthly => new DateOnly(today.Year, today.Month, 1),
        _ => today,
    };
}
