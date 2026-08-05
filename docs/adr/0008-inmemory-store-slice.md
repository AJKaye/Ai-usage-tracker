# ADR 0008 — In-memory store for the vertical slice (transitional)

**Status:** Accepted, transitional (2026-08-04) · **Decider:** engineering (build-time constraint)

## Context
The target stack (ADR-0001) is ClickHouse + Postgres + Kafka via Docker. The current dev machine has **no Docker** (and had no .NET SDK until installed this session — ADR-0002). We needed a genuinely runnable, tested vertical slice now, not a stack that can only run elsewhere.

## Decision
Ship an **in-memory `IEventStore`** (`UsageTracker.Storage.InMemory`) satisfying the exact production contract, wired at the composition root. All other modules (normalization, cost, ingest API) are unaffected. Tenant identity in the slice is a request header pending real `IIdentityProvider`/`ITenantResolver` wiring.

## Consequences
- (+) The full ingest→normalize→cost→store→summary path **runs and is tested here** (27 tests green, live server verified) without Docker.
- (+) Proves the storage seam: `EventStoreContractTests` is an abstract suite the in-memory store passes today and the ClickHouse store will inherit unchanged (Phase 1). Swapping stores is a one-line composition-root change.
- (−) Not durable; not the production store. This ADR is **transitional** — superseded when the ClickHouse implementation lands and passes the same conformance suite.
- **To advance:** install Docker on the dev/build host (or run Phase 1 in CI/a cloud box), then implement `IEventStore`→ClickHouse, `IStreamBus`→Kafka, `IRelationalStore`→Postgres behind the existing contracts.
