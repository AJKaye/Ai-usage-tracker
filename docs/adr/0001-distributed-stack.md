# ADR 0001 — Full distributed reference stack (D1)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
The tracker must ingest high-volume telemetry across many surfaces and reconcile billing-grade cost. Options ranged from a single-node pilot to a fully distributed stack.

## Decision
Build to the distributed reference architecture: **ClickHouse** (event/trace analytics) + **Postgres** (relational/catalog/identity state) + **Kafka** (ingest stream + async pipelines) + horizontally-scaled OTLP receivers, designed for scale-out from the start.

## Consequences
- (+) Matches the ingestion-hot-path SLOs and the "many platforms" scope; the storage roles are the pattern OpenMeter/Langfuse-class tools converge on (ARCHITECTURE.md §8.1).
- (−) Heavier ops burden and local-dev footprint; requires Docker/K8s. See ADR-0008 for how the current dev box (no Docker) is handled without compromising the target.
- Each store sits behind a contract (`IEventStore`/`IRelationalStore`/`IStreamBus`) so the engine choice is swappable.
