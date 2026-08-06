# Backup, Restore & Disaster Recovery

## solo (embedded SQLite) — verified

**Backup (two options):**
- **Portable (recommended):** `GET /v1/export` per tenant → a versioned JSON bundle.
  Store off-box. Content-free (usage/cost only), so it's safe to retain per your
  data-lifecycle policy.
- **File-level:** copy `usage-tracker.db` (+ `-wal`/`-shm` if present) while stopped,
  or use SQLite online backup while running.

**Restore:** `POST /v1/import` the bundle (idempotent; can restore into a fresh
tenant), or drop the `.db` back next to a stopped binary and start.

**Verified:** export→import round-trips with cost intact, across store
implementations and into a re-homed tenant (`DataPortabilityTests`, and live on the
exe).

**RPO/RTO (solo):** RPO = your export cadence (e.g. hourly cron of `/v1/export`);
RTO = time to copy a binary + import a bundle (minutes).

## distributed tier — designed (pending infra)

- **ClickHouse:** partitioned by time + tenant; back up via `BACKUP`/object-store
  snapshots; crypto-shred a subject by destroying its per-subject key (right-to-delete
  without rewriting partitions — the `SubjectKeyVault` mechanism, Phase 7).
- **Postgres:** PITR (WAL archiving) + periodic base backups for catalog/relational
  state.
- **Kafka:** at-least-once + idempotent dedup by `(tenant, span)` means a consumer
  replay after broker loss re-processes without duplicating — the chaos test
  ("kill a broker mid-load → zero lost events") validates this once a cluster exists.
- **Cross-tier DR:** the `/v1/export` bundle is the tier-independent escape hatch — a
  distributed outage can be served from a re-imported bundle in a `solo` binary.

*Not yet exercised on a live cluster; the seams (idempotent import, contract-parity
stores, per-subject keys) are built and tested.*
