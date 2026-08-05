# ADR 0006 — All four compliance bars + Regulatory Governance page (D6)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
The product targets regulated enterprises. Rather than pick one compliance regime, the owner required designing toward all of them simultaneously, plus an in-product page explaining how each is met.

## Decision
Design toward **SOC 2 Type II + GDPR/data-residency + HIPAA + FedRAMP/air-gap simultaneously**, shift-left (controls designed from Phase 0). Ship a dedicated **Regulatory Governance page** that renders the control→requirement matrix so compliance claims equal the code's real state.

## Consequences
- (+) No expensive re-architecture to add a regime later; the strictest constraints (FedRAMP air-gap, HIPAA encryption, GDPR erasure) shape the design up front.
- (+) `GOVERNANCE.md` is the single source of truth for the page — it can't drift into marketing.
- (−) Significant cross-cutting work (mTLS, FIPS, crypto-shredding, audit) concentrated in Phase 7; every phase carries a compliance status obligation.
- **Not a certification.** `GOVERNANCE.md` is a design-time control register; no row claims "Certified" without an external audit.
