# ADR 0009 — Lead with a zero-infra downloadable executable (B3)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner · **Amends:** ADR-0001 (D1), ADR-0008

## Context
The owner's integration goal: **"as easy to integrate as possible, so it can be a downloadable `.exe`."** ADR-0001 committed to a distributed stack (ClickHouse+Kafka+Postgres) — powerful for scale, but it requires Docker/K8s and so runs on very few machines out of the box (and not on the current dev box: no Docker/WSL/admin).

## Decision
Add an **embedded, zero-infrastructure tier** and make it the primary integration path:
- A **`solo` deployment profile** = embedded **SQLite `IEventStore`** + in-process pipeline, needing no server, daemon, admin, or Docker.
- Ship as a **.NET self-contained, single-file executable** (`usage-tracker.exe` / `usage-tracker`) that bundles the runtime + native SQLite — the target machine needs *nothing* installed.
- Backend tier is chosen by config (`USAGETRACKER__PROFILE`): `solo` (SQLite) → `standard` (Postgres) → `distributed` (ClickHouse+Kafka+Postgres). All satisfy the same `IEventStore`/`IStreamBus`/`IRelationalStore` contracts, so it is one product, not a fork.

This **amends but does not revoke D1**: the distributed stack remains the scale tier for SaaS/large self-host. Reach is added *below* it, not swapped for it.

## Consequences
- (+) Runs on essentially any machine .NET targets (win/linux/macOS × x64/arm64) — laptops, edge, air-gapped, pilots — with a double-click.
- (+) Verifiable locally: the SQLite store passes the same `EventStoreContractTests` as every other backend; the published exe was run here (no .NET/Docker) and persisted data across a restart.
- (+) Smooth upgrade path: a pilot on `solo` scales to `distributed` by changing config, because callers depend only on contracts.
- (−) The embedded tier's `SummarizeAsync` aggregates in-process (fine at solo volumes; the distributed store aggregates in SQL).
- (−) `standard`/`distributed` `IEventStore` wiring is incomplete (need infra to build/verify) — they fail fast with a clear message rather than mis-storing.
- Note: the SQLite dependency surfaced a transitive CVE (NU1903 / SQLitePCLRaw 2.1.11) caught by the `-warnaserror` gate; pinned forward to `bundle_e_sqlite3` 3.0.5.
