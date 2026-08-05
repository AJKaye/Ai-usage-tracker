# ADR 0003 — Self-host + SaaS from day one (D3)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
Enterprise buyers of a cost/usage tool handling sensitive data frequently require on-prem/air-gapped deployment; a SaaS offering serves faster onboarding. Building one then retrofitting the other is costly.

## Decision
Every artifact must run **air-gapped self-host** (Docker Compose + Helm/K8s) **and** as **multi-tenant SaaS**. Dual-mode is a first-class constraint validated continuously (compose bootstrap in Phase 0; at scale in Phase 10).

## Consequences
- (+) No painful retrofit; air-gap needs (offline pricing bundle, no outbound calls on critical paths) shape the design early — see the offline `IPriceCatalogSource`, already implemented.
- (−) Upfront architecture cost (no hard dependency on a specific managed cloud service on the critical path).
- Tenancy model (ADR-0005) supports single-tenant (count=1) self-host and pooled SaaS from the same schema.
