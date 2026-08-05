# ADR 0004 — React + TypeScript SPA (D4)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
The product needs a data-dense enterprise analytics UI (usage/cost/allocation/efficiency dashboards + the Regulatory Governance page) with a clean boundary to the .NET backend.

## Decision
**React + TypeScript SPA**, consuming the versioned query API and the centralized design system (`design-system/dist/tokens.{css,ts}`).

## Consequences
- (+) Largest ecosystem for enterprise data-viz/tables; clean API boundary; hires easily.
- (+) Consumes the design tokens directly, so theming/white-label/dark-mode are inherited, not re-solved (see ADR-0007).
- (−) A second language alongside the C# backend; mitigated by an OpenAPI-typed client.
- Built in Phase 8; the design-system CI gates land in Phase 0 so every UI increment inherits them.
