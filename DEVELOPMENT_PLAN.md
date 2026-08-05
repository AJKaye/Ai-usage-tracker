# AI Usage Tracker — Multi-Phase Development Plan

> **Read [`PROJECT_CONTEXT.md`](./PROJECT_CONTEXT.md) first** — it holds the mission, the six locked decisions (D1–D6), the non-negotiable principles, and the module map this plan executes against. This document is the *how and when*. [`ARCHITECTURE.md`](./ARCHITECTURE.md) is the *what and why*.

**Last updated:** 2026-08-04 · **Overall status:** Phase 0 substantially done; Phase 1 embedded tier built + verified (zero-infra `.exe`), distributed tier infra-blocked. See per-phase status.

---

## How to use this plan

- Phases are **mostly sequential** but overlap where noted; security, testing, and docs are **continuous across all phases**, not a final step.
- Each phase has: **Goal · Deliverables (checklist) · Key modules · Exit criteria (Definition of Done) · Verification**.
- **Check off deliverables in this file as you complete them**, and advance "Current phase" in `PROJECT_CONTEXT.md`.
- Every phase inherits the four principles (modularity, security, efficiency, integration) **plus Progressive Deployment (below)** as acceptance criteria — they are not re-listed each time.
- Estimates are rough order-of-magnitude for a small senior team; treat as sequencing guidance, not commitments.

> **★ Design philosophy — Progressive Deployment (ADR-0009 / B3; standing principle for all phases).**
> **One product, one codebase, storage & infra swappable by config — packaged so it runs on the widest possible range of machines with the lowest possible barrier to first use.** The concrete commitments every phase must honor:
> 1. **Embedded-first, scale-optional.** A feature must work in the **`solo`** profile (embedded SQLite, in-process pipeline, *zero external infrastructure* — the downloadable single-file `.exe`) **and** in `distributed` (ClickHouse/Kafka/Postgres). If a feature can only exist with server infra, that's a design smell — push the capability behind a contract with an embedded implementation, or gate it as an explicit scale-tier-only feature and say so.
> 2. **Backend choice is config, never a fork.** `USAGETRACKER__PROFILE` (`solo`/`ephemeral`/`standard`/`distributed`) selects implementations at the composition root. No "community vs enterprise" code split; the same binary spans laptop → server → cluster.
> 3. **Every swappable backend passes the same conformance suite** (e.g. `EventStoreContractTests`). That equivalence is what makes the profile switch safe rather than a downgrade.
> 4. **Distribution is a deliverable, not an afterthought.** Self-contained single-file publish for win/linux/macOS × x64/arm64 stays green in CI from Phase 1 onward; "double-click and it works, nothing installed" is a release gate.
> 5. **Zero-infra ⇒ air-gap-native.** The `solo` binary makes no outbound calls on any critical path (offline pricing bundle, local store) — which is also the FedRAMP/air-gap posture (D6). Reach and compliance are the same lever.
> **Why:** it removes the pilot barrier (evaluate today, no procurement/infra project), gives customers a fork-free path from trial to cluster, and reaches locked-down/air-gapped/edge environments SaaS-only tools can't. It amends **D1** (distributed stays the scale tier) — it does not replace it.

> **⚑ Slice status (2026-08-04):** a **walking-skeleton vertical slice** exists under `src/` + `tests/` (see `README.md`). It genuinely delivers, compiled + tested on .NET 10: the module **contracts** (Phase 0), the canonical model behind `IEventStore` (Phase 1, in-memory impl only — no ClickHouse/Kafka/Postgres yet), and the **normalization + ingest + query** path with both correctness keystones golden-tested (Phase 2 MVP core) and the **3-tier cost engine + offline seed catalog** (Phase 3 core). Everything else in every phase — real backends, OTLP wire receiver, reconciliation, other archetypes, FinOps, security/tenancy hardening, SPA, MCP — is **not built**. The checkboxes below are the full target; treat them as open unless `README.md` says the slice covers them.

**Phase overview**

| # | Phase | Outcome | Rough size |
|---|-------|---------|-----------|
| 0 | Foundations & Governance | Repos, contracts, CI/CD, threat model, governance mapping | 3–4 wk |
| 1 | Canonical Model & Storage Core | Schema + ClickHouse/Postgres/Kafka behind interfaces | 3–5 wk |
| 2 | Ingestion — OTLP + Normalization (MVP) | Ingest `gen_ai.*`, normalize, persist, query one event end-to-end | 4–6 wk |
| 3 | Cost Engine & Price Catalog | 3-tier costing, snapshotting, estimated cost | 4–6 wk |
| 4 | Reconciliation & Provider Connectors | Realized-cost pull, estimated-vs-reconciled delta | 4–6 wk |
| 5 | Multi-Archetype Ingestion | Proxy, usage-event API, Adapter SDK, closed surfaces | 5–7 wk |
| 6 | FinOps, Allocation & Serving API | FOCUS views, tag-free allocation, cost-per-outcome, query API | 4–6 wk |
| 7 | Security, Tenancy & Compliance Hardening | RLS, SSO/SCIM, audit, crypto-shredding, air-gap, FIPS | 5–7 wk |
| 8 | Web UI & Regulatory Governance Page | React SPA, dashboards, governance page | 5–7 wk |
| 9 | MCP Face, Scores & Ecosystem | MCP server, score aggregation, thin SDKs | 3–5 wk |
| 10 | Scale, Multi-Tier Packaging & GA Readiness | Signed single-binary (★) + Compose + Helm, load/chaos tests, pen test, GA | 4–6 wk |

---

## Phase 0 — Foundations & Governance

**Goal:** Stand up the skeleton so every later phase drops into place: repo/solution structure, the contract packages that make modularity real, CI/CD with security gates, and the governance artifact that anchors the compliance work (D6).

**Deliverables** *(status 2026-08-04 — verified on .NET 10, 27 tests green)*
- [x] Monorepo layout; **`.NET 10` solution** (`AiUsageTracker.slnx`) with `src/`, `tests/`, `deploy/`, `docs/`, `plugins/`.
- [x] **Contract package** (`UsageTracker.Contracts`, v0.1) for every §5 interface — interfaces + DTOs only. Compiles clean. *(single package for now; split into `.Contracts.*` sub-packages if versioning granularity demands it.)*
- [x] Plugin loading harness (`AssemblyLoadContext`, `UsageTracker.Plugins`) + reference Cursor adapter under `plugins/` + **tested contract-version rejection gate** (major mismatch & too-new minor both refused).
- [~] CI/CD: build + analyzer + unit-test gates **done** (`.github/workflows/ci.yml`, `-warnaserror`, palette gate). **SBOM / dependency+secret scanning / image signing — NOT yet** (finish before Phase 0 close).
- [ ] **Threat model v0** (STRIDE per module boundary) + security-controls register. *(control register exists in `GOVERNANCE.md`; STRIDE doc still to write.)*
- [x] **`GOVERNANCE.md`** — four-framework control→requirement→module→status matrix authored (honest "designed vs implemented" status; backs the Phase 8 governance page).
- [~] Local dev bootstrap — `deploy/docker-compose.yml` (ClickHouse+Postgres+Kafka+API) + `Dockerfile.ingestion-api` **authored and YAML-syntax-checked**, but **NOT `docker compose up`-verified (no Docker on the dev box)**. Needs a Docker host to prove.
- [x] ADR folder — D1–D6 (0001–0006), B1/B2 (0007), + the transitional in-memory-store decision (0008).
- [~] **Design-system CI gates**: palette validator wired into CI. Style Dictionary build + stylelint no-raw-values rule still to wire (design system authored under `design-system/`).

> **Also delivered beyond the original checklist:** the **`IEventStore` contract-conformance test base** (`EventStoreContractTests`) — the concrete "every implementation passes the same suite" modularity mechanism; ClickHouse will inherit it in Phase 1.

**Key modules:** all contracts; Platform (Config, Secrets abstraction stubs).

**Exit criteria:** a developer clones, runs one command, and gets the full backing stack + a passing pipeline that builds the solution, runs tests, and produces a signed image with an SBOM. `GOVERNANCE.md` exists with the four frameworks scaffolded.

**Verification:** CI green on an empty-but-wired solution; `docker compose up` healthchecks pass; the reference plugin loads and is rejected when its contract version mismatches.

---

## Phase 1 — Canonical Model & Storage Core

**Goal:** Implement the `Session → Trace → Span` model and the storage engines **behind their interfaces**, so everything above is storage-agnostic. **Reframed by ADR-0009 (B3):** lead with the embedded tier (runs anywhere, verifiable now); server backends are the scale tier (need infra → built + CI-verified, not local).

**Deliverables** *(status 2026-08-04)*
- [x] Canonical model types (`Span` + multi-bucket tokens + coarse-surface fields) — done in the slice, reused as-is.
- [x] **`IEventStore` → embedded SQLite** (`UsageTracker.Storage.Sqlite`) — the zero-infra `.exe` backend; tenant-scoped SQL (RLS-equivalent), idempotent by `(tenant, span)`; **passes the full `EventStoreContractTests`**. Verified on this box + in the published exe (survives restart).
- [x] **Deployment profiles + config-driven selection** at the composition root (`solo`/`ephemeral`/`standard`/`distributed`) — the one-line backend swap, ADR-0009.
- [x] **Self-contained single-file publish** (win/linux/macOS × x64/arm64) wired into CI (`publish-exe` job); win-x64 exe built + run here.
- [ ] `IEventStore` → **ClickHouse** (typed columns, partitioning for retention/crypto-shred). *Contract only — the `IEventStore` seam exists and the conformance suite is ready, but no ClickHouse implementation is written yet (infra-blocked: no Docker/WSL/admin here).*
- [ ] `IRelationalStore` → **Postgres** (RLS, `tenant_id` every table). *Contract only (`IRelationalStore` declared in `StorageAndStream.cs`); no Npgsql implementation yet.*
- [ ] `IStreamBus` → **Kafka** (ingest topic, DLQ, consumer groups, idempotent dedup). *Contract only (`IStreamBus` declared); no Kafka implementation yet. The in-process `IIngestChannel` is the slot-in seam.*
- [ ] Migration tooling for the server DBs; seed/fixture loader.
- [x] Golden-dataset harness — the canonical fixtures live in `EventStoreContractTests` (every store impl runs them).

> **Verified vs pending (corrected 2026-08-05 after audit):** the embedded/`.exe` half of Phase 1 is **done and runs on this machine**. The distributed half is **contract-only** — the interfaces (`IRelationalStore`/`IStreamBus`) and the shared conformance suite exist, but **no ClickHouse/Postgres/Kafka code is written**; `Program.cs` fail-fasts (`NotSupportedException`) on the `standard`/`distributed` profiles rather than mis-storing. Finishing them is "implement behind the existing contract + pass the same tests in CI" — not a redesign — but it is not yet started. To do it locally: install Docker, then bring up `deploy/docker-compose.yml`.

**Key modules:** Canonical Store; contracts.

**Exit criteria:** a canonical event round-trips through Kafka → ClickHouse and is queryable; swapping `IEventStore` to an in-memory fake requires only DI config (proves modularity).

**Verification:** integration tests write and read events across all three engines; RLS blocks cross-tenant reads in a two-tenant fixture; an in-memory `IEventStore` passes the same contract test suite as the ClickHouse one.

---

## Phase 2 — Ingestion (OTLP) + Normalization — the MVP

**Goal:** The first end-to-end slice from `ARCHITECTURE.md` §8.3 step 1: accept OTel `gen_ai.*`, normalize, persist, query.

**Deliverables** *(status 2026-08-04 — verified on .NET 10, 43 tests green + live OTLP curl against the published exe)*
- [~] **OTLP receiver** — **HTTP/JSON `POST /v1/traces` done** (real `ExportTraceServiceRequest` envelope: resourceSpans→scopeSpans→spans, AnyValue attrs; `UsageTracker.Ingestion.Otlp`). Semconv keys **pinned** in `SemConv.cs` behind the mapper. **gRPC transport not yet** (HTTP is the `solo`-friendly path; gRPC is additive later).
- [x] **`ITokenNormalizer`** subset-vs-additive — done in Phase 1 slice; Phase 2 confirms it survives the mapping (golden `SpanMapperTests`).
- [x] **`ISpanMapper`** — `GenAiSpanMapper` (primary) + `OpenInferenceSpanMapper` (second dialect) + `SpanMapperRegistry`; granularity tagged on the span.
- [x] **Async hot-path** — `IIngestChannel` (in-process `System.Threading.Channels`, bounded, backpressure) + `IngestConsumer` background service does cost+persist off the request. **This is the seam Kafka slots behind** (Phase-1 distributed remainder).
- [x] Backpressure (bounded channel, `Wait` mode — no silent drop); idempotent dedup (store keys `(tenant, span)`); poison-event isolation in the consumer.
- [~] Tenant resolution done (header; `ITenantResolver`/OIDC in Phase 7). Per-event **audit trail deferred to Phase 7** (`IAuditSink` contract exists). Oversized (413) + malformed (400) payloads rejected without partial ingest — tested.

> **Verified:** golden mapper suite (subset/additive, both dialects) + OTLP parser tests + async E2E (accept→drain→queryable) green; the **published `usage-tracker.exe` ingested a real 2-span OTLP body** and produced correct per-provider token math and cost (anthropic 1000/$0.01505, openai 1000/$0.0083). **Load-test for the &lt;10ms p99 SLO not yet run** (needs a load harness; the async design is in place).

**Key modules:** Ingestion Gateway; Normalization Engine; Canonical Store.

**Exit criteria:** an OTel-instrumented sample app (and a raw OTLP curl) produces spans that appear correctly normalized and queryable, with tokens bucketed correctly for at least one subset-provider and one additive-provider.

**Verification:** golden-test suite for token normalization is green (this is the correctness keystone); load test shows the hot-path SLO holds; a deliberately malformed/oversized payload is rejected without data loss.

---

## Phase 3 — Cost Engine & Price Catalog

**Goal:** Turn normalized usage into **estimated cost** via the 3-tier engine over a modular, versioned catalog.

**Deliverables** *(status 2026-08-05 — built + verified on .NET 10, 76 tests green under `-warnaserror`; branch `phase-3-cost-engine`. Full design: `docs/phase-3-cost-engine-design.md`.)*
- [x] `ICostTier` pipeline: (1) ingested-USD → (2) price-map → (2.5) coarse-unit → (3) tokenize-then-price → (fallback) unpriced; each tier independently testable/replaceable (`IngestedUsd`=10, `PriceMap`=20, `CoarseUnit`=30, `Tokenized`=40, `Unpriced`=90).
- [x] `IPriceCatalog` with the **composite key** (model × context-tier × service_tier × batch × region × deployment-type) and **date-stamped/versioned rates** — multiple rate *variants* per model, most-specific-wins + date-effective resolution.
- [~] `IPriceCatalogSource` implementations: `OfflineBundleCatalogSource` (extended), **`LiteLLMCatalogSource`** (parses `model_prices_and_context_window.json`), and **`HttpCatalogSource`** (live-sync over an *injected* `HttpMessageHandler` — no real network in tests). **Signed offline bundle** import done (`SignedOfflineBundleSource` + `EcdsaBundleVerifier`, D6/FedRAMP). *Cloud-specific SKU parsers (Google Catalog / Azure Retail / AWS Price List shapes) are a later add behind the same seam.*
- [x] **Rate snapshotting** — `RateSnapshot` stores the whole resolved `ModelRate` + provenance per event; `ICostRecomputer` reproduces the original cost from the snapshot (stable) or re-prices against the live catalog. `/v1/spans/{id}/recompute`.
- [x] Handlers for the §5 gotchas: multi-rate inputs (#2), reasoning-as-output (#3), batch discount (#4), tiered/context pricing whole-request re-rate (#5), modality (#6), tool surcharges (#7), per-hour `pricing_mode` (#8), region/tier multipliers (#9), cloud-set/region variants (#11), tokenizer drift (#10), date-effective (#14). *(#12 reconciliation = Phase 4; #13 response quirks = Phase 2/5 parsing.)*
- [x] Per-`granularity` pricing path (tokens vs credits vs seats vs requests) — `CoarseUnitCostTier` + `UnitRate`, so Phase 5 coarse surfaces plug in with no engine change.

**Key modules:** Cost Engine; Price Catalog.

**Exit criteria:** every event carries an estimated cost with its snapshotted rate; a golden "gotcha suite" priced against hand-computed expected values passes. **✅ Met** — see the §5 gotcha matrix in `docs/phase-3-cost-engine-design.md`; 76 tests green.

**Verification:** gotcha golden tests (one case per §5 item) green; swapping the catalog source from live to offline-bundle is a config change; recomputing a historical day after a price change reproduces the original cost. **✅** golden suites green (`RateSnapshotTests`, `CoarseUnitCostTests`, `CompositeKeyCatalogTests`, `AdditiveCostTests`, `CatalogSourceAndTier3Tests`, `Phase3WireFlowTests`); recompute-after-price-change reproduces the original (`RateSnapshotTests.Recompute_after_price_change...`); source swap is a DI/config change.

> **Phase-3 remainder:** cloud-provider-specific pricing-API parsers (Google/Azure/AWS SKU shapes) behind the existing `IPriceCatalogSource` seam; a real BPE tokenizer (tiktoken/Claude) behind `ITokenizer` in place of the heuristic. Both are additive — no redesign.

---

## Phase 4 — Reconciliation & Provider Connectors

**Goal:** Add the **authoritative billing** layer and the estimated-vs-reconciled delta (`ARCHITECTURE.md` §8.3 step 2).

**Deliverables** *(status 2026-08-05 — built + verified on .NET 10, 87 tests green under `-warnaserror`; branch `phase-4-reconciliation`, stacked on Phase 3.)*
- [~] `IBillingConnector` implementations for the realized-cost APIs: **OpenAI Costs** and **Anthropic `cost_report`** done (Anthropic connector surfaces the Priority-Tier exclusion, §5 #12). *Azure Cost Management / GCP BigQuery / AWS CUR are later adds behind the same contract + injected-`HttpClient` seam.*
- [x] `IReconciler` (`CostReconciler`) — sums estimated cost from stored spans, pulls realized rows from connectors, computes the **delta per provider** at the tenant-day grain; unmatched providers surface as estimate-only lines.
- [~] Resumable pulls + secrets via `ISecretProvider` (key resolved by name, never config; missing secret throws). *Scheduling/checkpointing of recurring pulls is a later add (the pull is date-windowed and idempotent, so it's a scheduler wrapper, not a redesign).*
- [~] Reconciliation views/materializations: `IReconciliationStore` seam + embedded `InMemoryReconciliationStore` (solo tier). *ClickHouse-backed materialization satisfies the same contract in the scale tier (infra-blocked here — no Docker).*
- [x] **Air-gap posture:** connectors are optional; with none configured the estimate stands and `ReconciledAgainstBilling=false` is surfaced (not silently zeroed). A connector failure degrades gracefully — estimate remains, run still succeeds.

**Key modules:** Reconciliation Service; Cost Engine; Canonical Store.

**Exit criteria:** for a connected provider, a day of estimated cost reconciles against pulled realized cost with an explainable delta. **✅ Met** — `BillingConnectorTests.Connectors_feed_the_reconciler_end_to_end` (estimate 12.00 vs realized 12.34 → delta 0.34, per-provider).

**Verification:** integration test with recorded/mocked provider billing responses; delta math validated against a known fixture; a connector failure degrades gracefully (estimate remains, reconciliation retries). **✅** `ReconcilerTests` + `BillingConnectorTests` (canned HTTP responses, no network) + `ReconcileEndToEndTests` (API `/v1/reconcile`); connector-failure graceful degradation tested; **live-verified on the zero-infra exe** (`/v1/reconcile` → estimate stands, air-gap flagged).

> **Phase-4 remainder (additive, no redesign):** Azure/GCP/AWS billing connectors behind the same seam; recurring scheduled pulls with checkpoint persistence; ClickHouse reconciliation materialization (needs Docker).

---

## Phase 5 — Multi-Archetype Ingestion (breadth)

**Goal:** Cover the remaining three ingestion archetypes and the closed/coarse surfaces (`ARCHITECTURE.md` §8.3 step 3) — this is where "tracks *all* platforms and tools" becomes real.

**Deliverables** *(status 2026-08-05 — built + verified on .NET 10, 102 tests green under `-warnaserror`; branch `phase-5-multi-archetype`, stacked on Phase 4.)*
- [x] **OpenAI-compatible proxy** (`IProxyBackend`, `UsageTracker.Ingestion.Proxy`) — zero-instrumentation forwarder over an *injected* HttpClient (no network in tests); returns upstream bytes+status verbatim + a canonical span from the wire `usage`. **Streaming SSE handled** — merges Anthropic `message_start` (input) + `message_delta` (final output), OpenAI last-chunk usage. Token math deferred to the provider-aware normalizer.
- [x] **Usage-event API** — CloudEvents 1.0 ingest (`POST /v1/events`, `CloudEventParser` + `CloudEventMapper`) for coarse events (RPA units, seats, premium requests), routed through the credit/seat/request granularity → CoarseUnit cost path.
- [x] **Adapter SDK** — `UsageTracker.Adapters.Reference` (adapters + `AdapterRunner` with per-(tenant,source) checkpointing + at-least-once retry-on-failure). `IUsageAdapter` plugin contract + the ALC plugin-load seam were proven in Phase 0 (`ReferenceCursor` loaded by path). *A standalone template repo is a packaging nicety, deferred.*
- [x] Reference adapters: **Cursor** (real tokens, Phase-0 plugin), **Claude Code** (Anthropic Admin/OTel token shape — additive), **GitHub Copilot** (seats + premium requests — no tokens), **UiPath** (AI units — credits). Each maps to the correct granularity + cost path.
- [x] RAG-pipeline coverage validated — retriever/embedding/reranker spans arrive via the Phase-2 OTLP `gen_ai.*` path and map to the 9-kind taxonomy with the token cost path; confirmed end-to-end (`RagCoverageTests`), no new code needed.

**Key modules:** Ingestion Gateway; Ingestion Adapters (plugins); Normalization.

**Exit criteria:** usage from at least one surface of each archetype lands in the canonical store with correct granularity and cost path. **✅ Met** — proxy (wire), CloudEvents (usage-event), reference adapters (pull), OTLP RAG (from Phase 2); each priced via its correct tier.

**Verification:** proxy captures a real provider round-trip incl. cost; a coarse UiPath-style unit event prices via the credit path; a third-party-style adapter built only against the SDK (no core changes) ingests successfully. **✅** `ProxyBackendTests` (canned upstream, no network), `CloudEventTests` (UiPath AI-unit → $0.40 credit path + `/v1/events` E2E), `ReferenceAdapterTests` + `AdapterRunnerTests` (adapters built only against contracts, driven by the runner).

> **Phase-5 remainder (additive):** a live proxy HTTP endpoint wired into the API host (the backend + capture are done; exposing a passthrough route is a thin add); real network calls in the adapters (they yield representative spans today, proving the SDK seam); a published adapter template repo.

---

## Phase 6 — FinOps, Allocation & Serving API

**Goal:** Make the data *useful* — allocation, unit economics, and the query API the UI and integrators consume (`ARCHITECTURE.md` §8.3 step 5).

**Deliverables**
- [ ] **FOCUS-column** cost views (`ConsumedQuantity`/`ConsumedUnit`, `BilledCost`/`EffectiveCost`, etc.) over the reconciled layer.
- [ ] `IAllocationStrategy` incl. **tag-free/dimension-based allocation** (attribute shared-endpoint cost to team/user/agent/session/MCP-session from captured dimensions).
- [ ] `IUnitMetric` — cost per token/inference/call up to **cost-per-outcome**.
- [ ] **Query/serving API** (REST + OpenAPI, versioned; gRPC internal): usage, cost, allocation, efficiency (latency/TTFT/cache-hit/retry/fallback). p95 < 500 ms over 30-day windows (SLO §8).
- [ ] Backend-for-frontend shaping for the Phase 8 SPA.

**Key modules:** Allocation & Unit Economics; API/BFF; Canonical Store.

**Exit criteria:** a caller can retrieve, for a tenant, cost broken down by model/provider/team/feature and by outcome, in FOCUS terms, within SLO.

**Verification:** allocation sums to 100% of spend with no tags present; API contract tests + performance test at target query latency; FOCUS export validates against the spec's column definitions.

---

## Phase 7 — Security, Tenancy & Compliance Hardening

**Goal:** Elevate the shift-left security work into the full **D6** posture. (Security has been present since Phase 0; this phase closes the compliance gaps and proves them.)

**Deliverables**
- [ ] **Multi-tenancy** enforced end-to-end: Postgres RLS + ClickHouse row policies verified on every read path; tenant context flows through all services.
- [ ] **Identity:** OIDC/SAML SSO, SCIM provisioning, RBAC+ABAC via `IIdentityProvider`.
- [ ] **Zero-trust:** service-to-service mTLS; least-privilege service accounts.
- [ ] **Encryption:** TLS 1.3 everywhere; AES-256 at rest; envelope encryption via KMS/Vault; **FIPS mode** toggle (FedRAMP); **per-subject data keys**.
- [ ] **Audit:** immutable, tamper-evident `IAuditSink` across all admin/data access; exportable evidence.
- [ ] **Data lifecycle:** field-level encryption for opt-in captured content; configurable retention/redaction/residency per tenant/region; **right-to-delete via crypto-shredding** on ClickHouse partitions (GDPR/HIPAA).
- [ ] **Air-gap validation:** full offline run with signed offline pricing bundle; assert zero outbound calls on critical paths.
- [ ] Threat model → v1; penetration-test readiness checklist; update `GOVERNANCE.md` to "implemented" per control.

**Key modules:** all Platform services; cross-cutting middleware.

**Exit criteria:** each of the four frameworks' controls in `GOVERNANCE.md` maps to an implemented, tested control; a cross-tenant access attempt is provably impossible; an air-gapped deployment functions with no network egress.

**Verification:** automated tenancy-isolation test battery; crypto-shred a subject and confirm their content is unrecoverable while aggregates persist; static/dynamic security scans clean; air-gap smoke test passes with egress firewalled.

---

## Phase 8 — Web UI & Regulatory Governance Page

**Goal:** The React + TypeScript SPA (D4), including the owner-requested **Regulatory Governance page**.

**Deliverables**
- [ ] React + TS SPA scaffold; **imports `design-system/dist/tokens.css` + `dist/tokens.ts`** as the sole styling source; auth via platform SSO; tenant/workspace switching that also sets the tenant `data-theme` (white-label) + light/dark.
- [ ] **Storybook** as the interactive component gallery over the design tokens; visual-regression (Chromatic/Playwright) + axe gates added to CI (extends the Phase 0 design-system gates).
- [ ] Core dashboards: usage explorer (Session→Trace→Span drill-down), cost (estimated vs reconciled delta), allocation, efficiency (latency/TTFT/cache/retry/fallback), unit economics — built from design-system components + charts per `design-system/specs/charts.md` (invoke the `dataviz` skill for each new chart).
- [ ] **Regulatory Governance page** — styled entirely from tokens; renders, per framework (SOC 2 / GDPR / HIPAA / FedRAMP), how the solution meets each requirement, sourced from `GOVERNANCE.md`/an API so it stays truthful as controls evolve. Includes data-residency, retention, and audit-export controls surfaced to admins.
- [ ] Accessibility (WCAG 2.1 AA) per `design-system/specs/accessibility.md`. Branding is **neutral base (B1)**; SS&C/other tenants apply via the white-label theme mechanism — no SS&C in the base.
- [ ] **Progressive Deployment (★):** the built SPA is **embedded into the `solo` single-file binary** (served as static assets by the API host) so the downloadable `.exe` ships *with* its UI — no separate web server, no Node runtime on the target. Same bundle is servable standalone for the distributed tier. "Download exe → open browser → dashboard loads, zero infra" is a Phase-8 exit check.

**Key modules:** Web UI; Design System; API/BFF; Governance.

**Exit criteria:** an admin can see spend, allocation, and efficiency for their tenant and read exactly how each compliance requirement is met, with no stale/hand-maintained governance copy.

**Verification:** end-to-end UI tests (Playwright) against a seeded tenant; a11y audit passes; governance page reflects a control change made in `GOVERNANCE.md`/API without a code edit.

---

## Phase 9 — MCP Face, Score Aggregation & Ecosystem

**Goal:** Close the agentic-era loop (`ARCHITECTURE.md` §8.3 steps 4 & 6) and make integration effortless.

**Deliverables**
- [ ] **MCP server face** (`IMcpUsageProvider`) — usage queries as MCP tools (with `outputSchema`) and datasets as resources (`resources/subscribe` push), so agents read their own spend live; optional MCP-proxy interception correlating via `mcp.session.id` + `_meta` trace context.
- [ ] **Score aggregator** (`IScoreSink`) — generic `POST …/score` endpoint; attach externally-computed scores from any eval framework (be the aggregator, not the judge).
- [ ] Thin convenience SDKs (TS + Python at least) over OTLP/usage-event/proxy.
- [ ] Agent-framework integration guides (LangChain/LangSmith, OpenAI Agents SDK, Claude Agent SDK, CrewAI, AutoGen, LlamaIndex) — mostly config, per `ARCHITECTURE.md` §7.2.

**Key modules:** MCP Server Face; Score/Quality Aggregator; API.

**Exit criteria:** an agent queries its live spend via MCP; an external eval posts a score that attaches to the right span; a new integration is achievable from docs alone.

**Verification:** MCP client reads usage tools/resources; score round-trips onto a span and appears in the UI; a from-scratch integration following only the guide succeeds.

---

## Phase 10 — Scale, Multi-Tier Packaging & GA Readiness

**Goal:** Prove D1 (scale) and D3 (dual-mode) under real conditions, prove **Progressive Deployment (★) end-to-end across all three tiers**, and reach GA.

**Deliverables**
- [ ] **Embedded-tier distribution (★, first-class):** signed, versioned **single-file binaries** for win/linux/macOS × x64/arm64 published on release (the Phase-1 `publish-exe` job promoted to a signed release pipeline); a "download → run → works" smoke test per platform in CI; a one-page quickstart. This is the primary integration path — treat it with the same rigor as the cluster packaging below.
- [ ] **Self-host packaging:** production Docker Compose + **Helm charts / K8s manifests**; air-gap install runbook; upgrade/migration runbook.
- [ ] **SaaS packaging:** multi-tenant deployment topology, autoscaling for OTLP receivers + Kafka consumers, tenant onboarding/offboarding automation.
- [ ] **Load & soak tests** proving linear horizontal scale and the ingestion SLOs at target volume; **chaos tests** (broker/node loss → no event loss).
- [ ] Backup/restore + DR runbooks for all three stores; data-export/portability tooling.
- [ ] Full observability of the platform itself (dogfood OTel); on-call SLO dashboards + alerting.
- [ ] **Third-party penetration test** and remediation; compliance evidence package assembled from `GOVERNANCE.md` + audit logs.
- [ ] Versioning/compatibility policy for contracts, plugins, and APIs published.

**Key modules:** all; deploy/ops.

**Exit criteria:** the **same product** runs across all three tiers — `solo` single-binary (zero infra), self-host cluster (air-gapped), and multi-tenant SaaS — selected by config; sustains target throughput within SLO; survives node/broker failure without data loss; passes pen test; has an evidence package for the four frameworks.

**Verification:** the downloaded `solo` binary runs on a clean machine with nothing installed and serves the UI (★); load test at target EPS holds hot-path p99 on the distributed tier; kill a Kafka broker and a receiver mid-load → zero lost events; fresh air-gap install from the runbook succeeds end-to-end; SaaS tenant onboarded and isolated; a `solo`→`distributed` profile migration on the same data is documented and tested.

---

## Continuous workstreams (every phase, not a phase)

- **Security:** threat-model updates, dependency/secret/SBOM scanning in CI, per-endpoint authZ + audit + tenancy, `GOVERNANCE.md` kept current.
- **Testing:** contract tests per interface (every implementation must pass the same suite — this is how modularity is *enforced*), golden datasets (token normalization + cost gotchas are the keystones), integration + E2E, performance regression gates.
- **Docs:** `PROJECT_CONTEXT.md` living (current phase + Decision Log), ADRs for significant choices, per-module READMEs, integration guides, operator runbooks.
- **Modularity audits:** periodically confirm no cross-module DB access and every swappable point has ≥2 implementations or a fake proving the seam.
- **Progressive Deployment (★ philosophy above):** every phase keeps the **`solo` (zero-infra) profile whole** — a feature that only works with server infra is either pushed behind a contract with an embedded impl or explicitly labelled scale-tier-only. The **self-contained single-file publish stays green in CI** (win/linux/macOS × x64/arm64), and new backends must pass the shared conformance suite before a profile may select them. "Runs from a downloaded binary, nothing installed" is a standing release gate, not a Phase-10 task.
- **Design System:** the central styling authority (`design-system/`) is authored (tokens, specs, palette validator, living styleguide) and enforced from Phase 0 via CI gates (no-raw-values stylelint, CVD/contrast palette gate, stale-`dist/` check). Any UI in any phase styles against `dist/tokens.{css,ts}` only; new components extend the token set (primitive → semantic → component), never hardcode; new charts follow `specs/charts.md` + the `dataviz` skill; new tenants use the white-label `_template.json` flow. Verify a design change in `design-system/styleguide/index.html` (theme-switch it) before wiring into features.

---

## Cross-phase dependency notes

- Phase 2's **token normalizer** and Phase 3's **cost gotcha suite** are the two correctness keystones — invest in their golden datasets early; everything downstream trusts them.
- **Security, tenancy, and audit are threaded from Phase 0**; Phase 7 *hardens and certifies* rather than *introduces* them. Do not defer authZ/tenancy scoping on endpoints built in Phases 2–6.
- **Air-gap (D6)** constrains Phases 3 (offline catalog bundle) and 4 (connectors optional) — design those seams when the modules are first built, not retrofitted in Phase 7.
- **Dual-mode (D3)** is validated continuously via the Phase 0 compose stack and proven at scale in Phase 10.
- **Progressive Deployment (★, ADR-0009)** spans the whole plan: the `solo` embedded binary is buildable/runnable from Phase 1 (done), keeps its zero-infra property in every phase (embed the SPA in Phase 8 so `solo` has a UI; keep the single-file publish green in CI throughout), and is packaged as a first-class signed release in Phase 10 alongside the cluster artifacts. It **amends D1** (distributed = the scale tier), never replaces it.
