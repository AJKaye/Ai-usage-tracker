# Upgrade & Migration Runbook

## Upgrade (solo, in place) — verified

1. `GET /v1/export` per tenant (or copy `usage-tracker.db`) — backup first.
2. Stop the old binary.
3. Replace with the new `usage-tracker` binary (verify its `SHA256SUMS`).
4. Start it. The embedded store is read as-is; `ExportBundle.format` and the `/v1` API
   are versioned (see `VERSIONING.md`), so a compatible build reads existing data.
5. Confirm `GET /v1/platform/stats` (uptime resets, store type unchanged) and a
   dashboard spot-check.

**Rollback:** stop, restore the previous binary + the `.db` backup, start.

## solo → distributed migration — the designed path (seams verified, cluster pending)

The migration is a **config + data-export step, not a rewrite** — because every store
satisfies the same `IEventStore` contract and passes the same conformance suite, and
the export bundle is store-agnostic.

1. **Stand up the server tier** (`standard`/`distributed` profile — Postgres/ClickHouse
   /Kafka via `deploy/`). *Infra-blocked here; provision on a Docker/K8s host.*
2. **Export from solo:** `GET /v1/export` per tenant → JSON bundle(s). (Proven: the
   bundle round-trips across store implementations — `DataPortabilityTests`
   `Cross_store_migration_...`.)
3. **Point the new instance at the server backend** by config
   (`USAGETRACKER__PROFILE=distributed`) — no code change (composition-root switch).
4. **Import into the server store:** `POST /v1/import` per tenant. Idempotent by
   `(tenant, span)`, so a re-run is safe.
5. **Verify** `/v1/summary` totals match pre-migration per tenant; cut traffic over.

**Why this is low-risk:** the import is idempotent and tenant-scoped; the source `.db`
is untouched (roll back by pointing config back at `solo`). The only unverified link
is the *server store implementation itself* (ClickHouse/Postgres `IEventStore`), which
is the infra-blocked Phase-1 remainder — once it passes `EventStoreContractTests` in
CI (the gate is written), this runbook is end-to-end.
