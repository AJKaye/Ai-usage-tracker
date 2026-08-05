# ADR 0005 — Shared schema + row-level security (D5)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
Multi-tenant SaaS needs tenant isolation; self-host runs a single tenant. Options: shared-schema + RLS, schema/DB-per-tenant, or a hybrid.

## Decision
**Shared schema + row-level security**: `tenant_id` on every row, enforced by Postgres RLS + ClickHouse row policies. Self-host runs as a single-tenant instance (tenant count = 1). The design keeps DB-per-tenant possible for regulated/air-gapped tenants that demand physical isolation.

## Consequences
- (+) Densest/most efficient at scale; standard SaaS default; one migration path.
- (−) Isolation is logical, not physical — so it must be provably enforced. Every store contract is tenant-scoped on every call, and `EventStoreContractTests` asserts no cross-tenant read (verified in the slice; production RLS in Phase 7).
- (−) A bug in tenant scoping is a cross-tenant leak — hence the conformance suite every store implementation must pass.
