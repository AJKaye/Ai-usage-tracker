# On-Call & SLO Runbook

## SLOs (from PROJECT_CONTEXT §8)

| SLO | Target | Signal |
|-----|--------|--------|
| Ingest accept (hot path) | < 10 ms p99 | async channel accepts, heavy work off-path |
| No event loss under backpressure | 0 lost | bounded channel `Wait` (never drops) + idempotent `(tenant, span)` |
| Cost estimate available | < 1 s after ingest | consumer drains → `processed` advances |
| Query API | p95 < 500 ms / 30-day window | serving endpoints |

## The self-observability signal

`GET /v1/platform/stats` (public ops signal) reports:
- `uptimeSeconds`, `profile`, `store`
- `ingest.enqueued` / `processed` / `failed` / `queueDepth`

**Read it as:**
- `queueDepth` climbing + `processed` flat → consumer stalled or store slow
  (backpressure working — producers slow, nothing lost). Investigate the store.
- `failed` climbing → poison events (logged per-span; isolated, consumer stays up).
  Inspect logs for the failing `SpanId`/tenant.
- `enqueued − processed` large and not shrinking → drain can't keep up → scale the
  consumer (distributed tier: add Kafka consumers).

## Alerting (to wire when metrics infra exists)

- Page if `/health` non-200 for > 1 min.
- Warn if `queueDepth` > 50% capacity for > 5 min; page at sustained 90%.
- Warn on any `failed` rate > 0 over a window.
- Dogfood: the platform emits its own OTel; point it at a second instance (or a
  standard OTel backend) for historical SLO dashboards. *Full dashboards/alerting are
  infra-dependent; the raw signal (`/v1/platform/stats`) ships today.*

## Common actions

- **Consumer stalled:** check store health; restart is safe (channel is in-memory in
  solo, so in-flight-but-unqueued is bounded; distributed uses Kafka offsets).
- **Restore after data loss:** see `backup-restore-dr.md`.
- **Air-gap egress alarm:** in `solo`/`ephemeral` any outbound attempt fails closed
  (`AirGapViolationException`) — a thrown egress is a code/config bug, not a breach.
