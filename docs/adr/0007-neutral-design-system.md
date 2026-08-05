# ADR 0007 — Neutral design system + swappable chart palette (B1/B2)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
The product is white-label across tenants. The design system's base could bake in a specific brand (e.g. SS&C) or be brand-neutral; chart colors could chase brand fidelity or accessibility.

## Decision
- **B1** — the design-system base is **fully neutral/generic**; SS&C is just one example theme like any tenant. White-label = an extended theme overriding ~a dozen brand tokens.
- **B2** — charts default to a **generic, accessibility-validated palette template**; a tenant swaps it for brand hues and must clear the CVD/contrast validator before shipping.

## Consequences
- (+) Cleanest white-label architecture — no brand leaks into the foundation; the product looks vendor-independent out of the box.
- (+) Charts are color-blind-safe by default; brand palettes are gated in CI (`design-system/scripts/validate_palette.mjs`).
- (−) A tenant wanting exact brand chart hues may need to accept validator-driven adjustments (snap-to-passing).
- Realized in `design-system/` (three-tier CSS-custom-property tokens, light/dark, extended themes); the React SPA (ADR-0004) consumes it.
