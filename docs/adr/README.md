# Architecture Decision Records

Each ADR captures one significant, hard-to-reverse decision: context, the decision, and consequences. ADRs are append-only — supersede, never rewrite (mirrors the Decision Log in `PROJECT_CONTEXT.md`).

| ADR | Decision | Status |
|-----|----------|--------|
| [0001](0001-distributed-stack.md) | D1 — full distributed reference stack | Accepted |
| [0002](0002-dotnet-core.md) | D2 — .NET 10 / C# core | Accepted |
| [0003](0003-dual-mode.md) | D3 — self-host + SaaS from day one | Accepted |
| [0004](0004-react-spa.md) | D4 — React + TypeScript SPA | Accepted |
| [0005](0005-shared-schema-rls.md) | D5 — shared schema + row-level security | Accepted |
| [0006](0006-compliance-all-four.md) | D6 — all four compliance bars + governance page | Accepted |
| [0007](0007-neutral-design-system.md) | B1/B2 — neutral design system + swappable chart palette | Accepted |
| [0008](0008-inmemory-store-slice.md) | In-memory store for the slice (Docker absent) | Accepted (transitional) |
| [0009](0009-embedded-exe-reach.md) | B3 — zero-infra downloadable `.exe` (embedded SQLite `solo` profile) | Accepted (amends D1) |
| [0010](0010-embedded-columnar-analytics.md) | Self-contained is the product (embedded DuckDB columnar `analytics` profile); clustering is an evidence-triggered HA option | Accepted (amends D1, 0009) |
