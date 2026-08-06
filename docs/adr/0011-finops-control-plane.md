# ADR 0011 — FinOps control plane: budgets, alerts, anomaly detection, forecasting

**Status:** Accepted (2026-08-06) · **Decider:** Product owner · **Extends:** ARCHITECTURE.md §6 (FinOps)

## Context
Through Phase 10 the product was a **passive reporting hub**: it ingests AI usage from
every archetype, normalizes, costs, reconciles, allocates, and serves it — but only ever
tells you what you *already* spent, allocated, and how efficient you were. Two independent
code explorations confirmed that **budgets, spend alerts, cost-anomaly detection, and
forecasting appeared nowhere** in `ARCHITECTURE.md §6`, `DEVELOPMENT_PLAN.md`,
`PROJECT_CONTEXT.md`, or the code. Yet these are the first things an enterprise FinOps
buyer expects — the difference between a dashboard and a control plane. This was a genuine
capability gap, not an already-acknowledged infra-blocked remainder.

Owner decisions (this session): build the **full slice** (budgets + alerts + anomaly +
forecast); alerts land in an **in-app feed** (works in every profile) with an **optional
outbound webhook gated by `IEgressGuard`** (fails closed under air-gap); anomaly + forecast
use **transparent statistical methods** (z-score vs a trailing baseline; run-rate linear
projection) — explainable, deterministic, golden-testable, zero ML/infra.

## Decision
1. **Add an active FinOps control plane** as a new self-contained increment (Phase 11),
   reusing the existing seams rather than adding infrastructure:
   - **Budgets** (`Budget`/`IBudgetStore`) — a spend limit scoped to a dimension
     (whole-tenant or one team/model/provider/environment value) over a daily/monthly
     period. Scope reuses `DimensionAllocationStrategy.KeyFor` (§6.2 tag-free attribution);
     spend reuses `CostPerTokenMetric.TotalCost`.
   - **Evaluation** (`BudgetEvaluator`, pure) — spend-to-date vs limit → utilization,
     run-rate projected end-of-period, state (ok/warning/exceeded).
   - **Anomaly detection** (`CostAnomalyDetector`, pure) — z-score of the latest day vs a
     trailing-N-day baseline (mean ± k·stddev). A perfectly flat baseline (zero variance)
     flags any increase with a **null** z-score (undefined, not Infinity/NaN — the latter
     are also unserializable).
   - **Forecast** (`SpendForecaster`, pure) — spend-to-date + avg-daily-run-rate ×
     days-remaining.
   - **Alerts** — `BudgetScanService : BackgroundService` (mirrors `IngestConsumer`:
     poison-isolated, `PeriodicTimer` + injected `TimeProvider`) scans budgets + anomalies
     per tenant, writes an in-app `IAlertSink` feed, de-dupes per (budget, period, state) /
     anomaly-day, and — only when `USAGETRACKER__ALERT_WEBHOOK` is set — POSTs via an
     `IEgressGuard`-gated `WebhookNotifier`.
2. **One additive data-model seam.** `SpanQuery` gains an optional exclusive upper bound
   `Until`, and `IEventStore` gains `SummarizeByDayAsync` as a **default interface method**
   (query + in-process day-bucketing) so existing stores keep compiling; `SqliteEventStore`
   and `DuckDbEventStore` override it with `GROUP BY date()` pushdown. All three still pass
   `EventStoreContractTests` (the modularity contract holds).
3. **Estimated-cost layer, honestly scoped.** Budgets/alerts evaluate against the
   *estimated* cost (§4). Budget-vs-**reconciled** cost (§8.3 step 2) is a deliberate
   follow-up gated on the cloud billing connectors — not silently conflated.

## Consequences
- (+) The product is now an **active control plane**, not just reporting — the enterprise
  gap is closed.
- (+) **Self-contained**: the in-app alert feed + all evaluation work in every profile,
  including air-gapped. The optional webhook is the only outbound path and it **fails
  closed** under the D6 egress guard.
- (+) **Explainable + deterministic**: z-score and run-rate are golden-testable
  (`BudgetEvaluatorTests`, `AnomalyForecastTests`) and auditable — no opaque model.
- (+) **Verifiable here**: `BudgetApiEndToEndTests` drives create → ingest → status
  exceeded → scan raises alert → de-dupe → forecast → seeded anomaly → tenant isolation,
  all in-process. 198 tests green under `-warnaserror`.
- (−) Anomaly detection is a simple trailing-baseline z-score; seasonality/trend-aware
  methods (and real ML forecasting) are out of scope by choice.
- (−) Budgets track estimated, not reconciled, spend until the billing connectors land.
- (−) The de-dupe watermark and the in-app feed are in-process (bounded per tenant); a
  durable `IAlertSink`/`IBudgetStore` for the scale tier is a later swap behind the same
  contract, exactly like the other stores.

## Guidance for future agents
Keep the engine (`BudgetEvaluator`/`CostAnomalyDetector`/`SpendForecaster`) **pure** — that
is what makes it testable and reusable by both the API and the background scan. Any new
outbound alert channel MUST gate on `IEgressGuard` before the first network call (mirror
`WebhookNotifier`). New `IEventStore` backends must implement/override `SummarizeByDayAsync`
and pass `EventStoreContractTests`. When the billing connectors land, add budget-vs-reconciled
as an additive mode — don't replace the estimated path.
