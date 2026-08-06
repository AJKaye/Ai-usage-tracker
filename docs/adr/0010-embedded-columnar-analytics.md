# ADR 0010 — Self-contained is the product; clustering is an evidence-triggered option

**Status:** Accepted (2026-08-06) · **Decider:** Product owner · **Amends:** ADR-0001 (D1), ADR-0009 (B3)

## Context
ADR-0001 locked **D1: a full distributed reference stack** (ClickHouse + Postgres +
Kafka), and ADR-0009 added the zero-infra `solo` `.exe` *below* it while keeping the
distributed stack as "the scale tier." In practice the distributed tier stayed
**infra-blocked** end-to-end (no Docker/WSL/admin here) across every phase, and got
recorded as the perpetual "remainder."

Re-examining *why* the servers were chosen surfaced that they bought two things:
1. **Analytics performance** — columnar SUM/GROUP BY over large span sets (ClickHouse),
2. **Horizontal scale-out + HA** — many nodes, replication, failover (all three).

Only **(2)** actually requires multiple machines. **(1)** — the capability the
self-contained build genuinely lacked (SQLite aggregates in-process; see ADR-0009's
noted minus) — has an **embedded, in-process equivalent**: DuckDB, a vectorized
columnar OLAP engine whose native library ships inside the single `.exe`, exactly like
SQLite. And for *this* product — AI-usage metering, a downtime-tolerant FinOps/
observability workload at event rates typically in the tens-to-hundreds/sec — a single
node covers the overwhelming majority of real deployments. Clustering is premature
scaling absent evidence a node can't keep up.

## Decision
1. **Add an embedded columnar `IEventStore` (DuckDB)** as the `analytics` deployment
   profile (`USAGETRACKER__PROFILE=analytics`). It closes the one real capability gap —
   fast columnar roll-ups **in-engine** — with **zero infrastructure**, staying one
   downloadable file. It passes the identical `EventStoreContractTests` as every store.
2. **Reframe D1.** The distributed stack (ClickHouse/Postgres/Kafka + Helm/K8s) is
   **the multi-node horizontal-scale / HA option**, *not* a capability the
   self-contained product lacks. Its embedded equivalents ship today:

   | Scale server | Bought | Self-contained equivalent (in the `.exe`) |
   |---|---|---|
   | ClickHouse | columnar analytics | **DuckDB** (`analytics` profile) — *this ADR* |
   | Postgres | relational/catalog state | **SQLite** (`solo`) |
   | Kafka | durable ingest queue | bounded **`IIngestChannel`** (backpressure, idempotent `(tenant,span)`) |

3. **Clustering becomes evidence-triggered, not a goal.** Reach for the distributed
   tier only on a concrete signal — sustained ingest that pins a single beefy node
   (visible as `/v1/platform/stats` `queueDepth` climbing and never draining), or a
   hard availability SLA on the analytics itself. Because every backend sits behind the
   same contracts, that switch stays a **config change + data export/import** (the
   `/v1/export`→`/v1/import` migration substrate), never a rewrite.

## Consequences
- (+) The **capability gap is closed self-contained**: columnar OLAP with no server.
  The distributed tier's perpetual "infra-blocked remainder" is no longer a *missing
  capability* — it's an optional multi-node HA upgrade.
- (+) Verifiable here (unlike the servers): `DuckDbEventStore` passes the full
  conformance suite; the published exe ran the `analytics` profile, aggregated columnar,
  and survived a restart.
- (+) One product still — `solo`/`analytics`/`standard`/`distributed` are one binary,
  one contract set, selected by config.
- (−) Embedded engines are bounded by one machine's CPU/RAM/disk — they do **not**
  provide multi-node scale-out or HA. That remains the (now honestly-scoped, deferred)
  distributed tier, to be built + CI-verified with service containers if/when an
  evidence trigger or an owner requirement calls for it.
- (−) DuckDB returns integer `SUM()` as `BigInteger`; the store normalizes numeric
  extraction (noted so a future maintainer doesn't re-trip it).

## Guidance for future agents
Default to **self-contained** (`solo` for simple, `analytics` for high-volume columnar).
Treat the distributed stack as a scale/HA feature to justify with evidence, not a box to
tick. Keep new backends passing `EventStoreContractTests` — that equivalence is what
makes the tier switch safe.
