# AI Usage Tracker — Project Context (North Star)

> **This is the single source of truth for the project's intent, locked decisions, and working rules.**
> Every agent or engineer touching this product reads this file first and keeps it current. If a decision changes, update the Decision Log here in the same change — do not let this file drift from reality. Companion documents: [`ARCHITECTURE.md`](./ARCHITECTURE.md) (the *what/why* research substrate), [`DEVELOPMENT_PLAN.md`](./DEVELOPMENT_PLAN.md) (the *how/when* multi-phase roadmap), and [`GOVERNANCE.md`](./GOVERNANCE.md) (control→requirement mapping, once created in Phase 0).

**Last updated:** 2026-08-05 · **Current phase:** **Phase 6 done — FinOps, allocation & serving API (the data is now *useful*).** New `UsageTracker.FinOps` project: **tag-free `DimensionAllocationStrategy`** (100% of spend by team/user/session/model/provider/feature-from-metadata, `(unattributed)` bucket, no upstream tags), **unit economics** (cost per token/inference/**outcome**), **FOCUS-column projection** (`FocusRow` — token→tokens, coarse→ai_unit/request/seat), operational **efficiency** (latency/TTFT/cache-hit/error rate from spans), and the framework-agnostic **score aggregator** (`IScoreSink`). Serving API: `/v1/allocation`, `/v1/unit-economics`, `/v1/focus`, `/v1/efficiency`, `POST /v1/scores` + `GET /v1/spans/{id}/scores`. **113 tests green under `-warnaserror`**, additive/non-breaking; branch `phase-6-finops` (stacked on Phase 5). Live-verified on the zero-infra exe. gRPC + the p95<500ms load test are deferred (same as Phase-2 SLO harness). Prior: **Phase 5 done — multi-archetype ingestion ("tracks all platforms" is real).** All four ingestion archetypes now land in the canonical store: OTLP (Phase 2), **proxy** (`OpenAiCompatibleProxy` — zero-instrumentation forwarder over an injected HttpClient, verbatim passthrough + wire-usage capture incl. streaming SSE merge), **usage-event API** (`POST /v1/events` CloudEvents 1.0 → coarse credit/seat/request path), and **pull adapters** (`UsageTracker.Adapters.Reference`: UiPath credits, Copilot seats+requests, Claude Code additive tokens, Cursor; `AdapterRunner` with checkpoint + at-least-once retry). RAG (retriever/embedding/reranker) confirmed over the OTLP path. **102 tests green under `-warnaserror`**, additive/non-breaking; branch `phase-5-multi-archetype` (stacked on Phase 4). Prior: **Phase 4 done — reconciliation layer (estimated vs realized billing).** `CostReconciler` sums estimated cost from stored spans and pulls realized cost from `IBillingConnector`s (OpenAI Costs + Anthropic cost_report, over an *injected* HttpClient — no network in tests; keys via `ISecretProvider`), computing the **delta per provider** at the tenant-day grain (`IReconciliationStore` + embedded impl; `/v1/reconcile`). **Connectors are optional** — air-gap/no-connector leaves the estimate standing with `ReconciledAgainstBilling=false` surfaced; a connector failure degrades gracefully. Anthropic Priority-Tier exclusion (§5 #12) is surfaced. **87 tests green under `-warnaserror`**, additive/non-breaking; branch `phase-4-reconciliation` (stacked on `phase-3-cost-engine`). Live-verified on the zero-infra exe. Prior: **Phase 3 done — the cost engine handles the §5 gotchas end-to-end.** The 3-tier engine (now 5 tiers: IngestedUsd→PriceMap→CoarseUnit→Tokenized→Unpriced) prices over a **composite-key, date-effective** catalog with **multiple rate variants per model**; every event carries a **RateSnapshot** so historical recompute (`ICostRecomputer`, `/v1/spans/{id}/recompute`) reproduces the original cost after a price change. Covers batch (#4), context-length whole-request re-rate (#5), modality (#6), tool surcharges (#7), per-hour PTU (#8), geo/region multipliers (#9/#11), tokenizer drift (#10), date-effective (#14), and the coarse per-granularity path (#15, credits/seats/requests). Catalog sources: offline bundle (+ **signed** ECDSA bundle for D6 air-gap), **LiteLLM-format**, and **injected-HTTP live-sync** (no network in tests). **76 tests green under `-warnaserror`**, all additive/non-breaking; work on branch `phase-3-cost-engine`. Full design: `docs/phase-3-cost-engine-design.md`. Prior: **Phase 2 core done — real OTLP/HTTP ingestion is live in the `.exe`.** `POST /v1/traces` accepts the actual OpenTelemetry `ExportTraceServiceRequest` envelope → `ISpanMapper` (gen_ai.* + OpenInference dialects, semconv keys pinned) → async `IIngestChannel` + background consumer → cost + store. 43 tests green under `-warnaserror`; verified by live OTLP curl against the published `usage-tracker.exe` (correct additive/subset token math + per-provider cost). gRPC transport, the <10ms-p99 load test, and per-event audit are the Phase-2 remainder. **Phase 1:** embedded tier done + verified; distributed backends (ClickHouse/Kafka/Postgres) remain infra-blocked (no Docker) — the async channel is the seam Kafka slots behind. Prior: **Phase 1 embedded `.exe`.** A self-contained single-file `usage-tracker.exe` (no .NET/Docker/admin needed) runs the new **`solo` profile** = embedded **SQLite `IEventStore`** (passes the full conformance suite) + config-driven backend selection; ingest→normalize→cost→persist works and **survives restart**. A supply-chain CVE (NU1903 via SQLitePCLRaw 2.1.11) was caught by the `-warnaserror` gate and pinned forward. CI publishes the exe for win/linux/macOS × x64/arm64. **Remaining Phase 1:** the *scale-tier* server backends (Postgres/Kafka/ClickHouse behind the same contracts) — these need Docker, which this box can't run (no Docker/WSL/admin), so they're built + CI-verified, not local. See §2.5 below + `README.md`. Prior status: **Phase 0 substantially done + a Phase 2 vertical slice.** Built & verified on .NET 10 (8 projects, **27 tests green**, Release build clean under `-warnaserror`): the full §5 **contract surface**; the **plugin harness** (`AssemblyLoadContext`) with a reference adapter + a *tested* contract-version rejection gate; the **`IEventStore` contract-conformance suite** (the "every impl passes the same suite" modularity mechanism, made real); `GOVERNANCE.md`; ADRs 0001–0008; and the docker-compose self-host bootstrap (authored + syntax-checked, **not** runtime-verified — no Docker here). Plus the earlier vertical slice: ingest→normalize→cost→store runs end-to-end with both correctness keystones. Storage is still the in-memory `IEventStore` fake (Docker/ClickHouse absent). Remaining Phase 0 items (SBOM/scan/sign in CI, threat-model doc) and Phases 1/3–10 are open. See `README.md` for what works vs stubbed.

---

## 1. Mission

Build an **enterprise-grade, standalone central repository that tracks AI usage across every consuming surface** — direct model APIs (OpenAI, Anthropic, Gemini/Vertex, Azure Foundry, Bedrock), AI gateways, agent frameworks, MCP servers, RAG pipelines, IDE assistants, and RPA bots with embedded LLM steps — and computes **cost** and **efficiency** with billing-grade accuracy.

**The differentiator (from `ARCHITECTURE.md` §0):** be the **reconciliation hub** that ingests from all four ingestion archetypes, normalizes to one schema, and reconciles *estimated* per-event cost against *authoritative* provider-billing cost. No existing product spans all four archetypes and all surfaces. That gap is the product.

---

## 2. Locked decisions

These were decided by the product owner and are **not to be re-litigated** without an explicit new decision recorded in the Decision Log (§9). They shape every phase.

| # | Decision | Choice | Implication |
|---|----------|--------|-------------|
| D1 | **Scale posture** | **Full distributed reference stack** | ClickHouse (clustered) + Postgres + Kafka + horizontally-scaled OTLP receivers, built for scale-out from the start — not a single-node pilot. |
| D2 | **Core language / runtime** | **.NET 10 (LTS), C#** | ASP.NET Core for ingestion/API services; high-throughput, single-artifact deploys, strong enterprise + security tooling, FIPS-capable crypto. |
| D3 | **Deployment model** | **Both self-host and SaaS from day one** | Every artifact must run air-gapped on-prem (Docker Compose + Helm/K8s) *and* as a multi-tenant hosted service. Dual-mode is a first-class constraint, not a later port. |
| D4 | **Frontend** | **React + TypeScript SPA** | Clean API boundary to the .NET backend; enterprise-depth data-viz/tables; includes the Regulatory Governance page (D6). |
| D5 | **Multi-tenancy** | **Shared schema + row-level security** | Postgres RLS + ClickHouse row policies, `tenant_id` on every row. Self-host runs as a single-tenant instance (tenant count = 1); SaaS pools tenants. Design keeps DB-per-tenant possible for regulated/air-gapped tenants that demand physical isolation. |
| D6 | **Compliance bar** | **SOC 2 Type II + GDPR/data-residency + HIPAA + FedRAMP/air-gap — all four** | Shift-left: controls are designed in from Phase 0, not bolted on. Plus a **dedicated in-product "Regulatory Governance" page** that explains, per framework, how the solution meets each requirement (backed by `GOVERNANCE.md`). |

---

## 3. Non-negotiable principles

The product owner named four things as paramount. They are acceptance criteria for *every* piece of work, not aspirations.

1. **Modularity & replaceability.** Every component sits behind a versioned contract (interface + DTOs) so it can be upgraded or swapped without touching callers. If you cannot replace a component by writing a new implementation of its contract and changing configuration, the boundary is wrong. Concretely: storage engines, ingestion adapters, pricing-catalog sources, cost-engine tiers, allocation strategies, and identity providers are all **pluggable behind interfaces**. See §5.
2. **Security-first (enterprise-grade).** Zero-trust between services (mTLS), encryption in transit and at rest, secrets never in config or code, immutable audit logging, least-privilege everywhere, threat-modeled per module. Content capture (prompts/responses) is **opt-in and treated as potential PII/PHI**. Security work is *in every phase*, formalized in Phase 7.
3. **Efficiency.** The ingestion path is the hot path — it must sustain high event throughput with backpressure, batching, and async offload; nothing on the critical path blocks on pricing or reconciliation. Measured against explicit SLOs (§8).
4. **Ease of integration.** A new surface should be onboardable in minutes: point at the OTLP endpoint, drop in a gateway base-URL, or install a thin SDK. Ship first-class OTel compatibility, an OpenAI-compatible proxy, and a documented Adapter SDK so third parties (and future agents) add surfaces without core changes.

---

## 4. Reference architecture (summary — full detail in `ARCHITECTURE.md`)

Data flows left-to-right; every band is a replaceable module (§5).

```
INGESTION ──▶ NORMALIZATION ──▶ COST ENGINE ──▶ STORAGE ──▶ SERVING
(4 archetypes)  (canonical model) (3-tier + catalog) (CH/PG/Kafka) (API/UI/MCP)
```

- **Four ingestion archetypes** (offer all): OTLP receiver (`gen_ai.*`), proxy/gateway (OpenAI-compatible), usage-event API (CloudEvents), and pull adapters (provider billing APIs, IDE/RPA).
- **Canonical model:** `Session → Trace → Span`, with an extensible span `kind` (llm/agent/tool/chain/retriever/embedding/reranker/guardrail/evaluator) and the superset field set incl. **multi-bucket tokens** and **coarse-surface fields** (`granularity`, `units_consumed`, `unit_type`).
- **Wire schema:** OpenTelemetry GenAI `gen_ai.*` (primary) + OpenInference (second dialect, carries USD cost attrs). **Pin a version — the spec is Development-stability and will churn.**
- **Cost engine:** 3-tier fallback (ingested USD → price-map → tokenize). Composite-key, date-stamped price catalog seeded from LiteLLM's JSON. **Snapshot the rate at ingestion time**; keep **estimated cost and reconciled billing cost as separate layers** and surface the delta.
- **FinOps:** FOCUS columns for the reconciled layer; tag-free dimension-based allocation; unit economics up to **cost-per-outcome**; be a **score aggregator**, not an eval engine.
- **Storage:** ClickHouse (event/trace analytics), Postgres (catalog/billing/entitlement/identity state), Kafka (ingest stream + async pipelines).

**The #1 correctness bug to never get wrong** (`ARCHITECTURE.md` §3.4/§5): *subset-vs-additive token decomposition.* OpenAI/Google cached & reasoning tokens are **subsets already inside** the parent total — never add on top. Anthropic/Bedrock `input_tokens` is the **uncached remainder** — you **must add** cache tokens back. The token normalizer owns this rule and is covered by golden tests.

---

## 5. Module map & contract philosophy

The system is a set of independently deployable services and clearly bounded libraries. **Contracts live in dedicated, semantically-versioned packages** (`UsageTracker.Contracts.*`); implementations depend on contracts, never on each other. Adapters and pricing sources load as **plugins** (via `AssemblyLoadContext`) so surfaces are added without recompiling core.

| Module | Responsibility | Key swappable contract(s) |
|--------|----------------|---------------------------|
| **Ingestion Gateway** | Terminate OTLP / proxy / usage-event / adapter traffic; authenticate; enqueue | `IIngestChannel`, `IProxyBackend` |
| **Ingestion Adapters** (plugin) | Pull from closed surfaces (Cursor, Claude Code, Copilot, UiPath, provider billing) | `IUsageAdapter`, `IAdapterSchedule` |
| **Normalization Engine** | Token normalizer (subset/additive), schema mapper, granularity tagger | `ITokenNormalizer`, `ISpanMapper` |
| **Canonical Store** | Persist Session→Trace→Span; query analytics | `IEventStore` (→ClickHouse), `IRelationalStore` (→Postgres), `IStreamBus` (→Kafka) |
| **Cost Engine** | 3-tier costing; rate snapshotting | `ICostTier`, `IPriceCatalog`, `IPriceCatalogSource` (live-sync **or** offline signed bundle for air-gap) |
| **Reconciliation Service** | Pull realized-cost APIs; compute estimated-vs-reconciled delta | `IBillingConnector`, `IReconciler` |
| **Allocation & Unit Economics** | Tag-free allocation; cost-per-outcome | `IAllocationStrategy`, `IUnitMetric` |
| **Score / Quality Aggregator** | Ingest & attach externally-computed scores | `IScoreSink` |
| **MCP Server Face** | Expose usage as MCP tools/resources; optional MCP interception | `IMcpUsageProvider` |
| **API / BFF** | Query API, FOCUS cost views, backing-for-frontend | (REST/gRPC contract, OpenAPI-versioned) |
| **Web UI** | React SPA incl. Regulatory Governance page; consumes the **Design System** (`design-system/dist/tokens.{css,ts}`) | (consumes API contract + design tokens) |
| **Design System** | Central, themeable styling authority — token pipeline, specs, palette validator, living styleguide | CSS custom properties (`--*`) + `dist/tokens.ts`; theming via `[data-theme]` scopes. See `design-system/DESIGN_SYSTEM.md` |
| **Platform services** | Identity/SSO, Tenancy, Audit, Secrets, Config, Governance | `IIdentityProvider`, `ITenantResolver`, `IAuditSink`, `ISecretProvider` |

**Rules for module boundaries:** (1) no shared mutable database access across modules — go through the owning module's contract; (2) breaking a contract = a major version bump + migration note; (3) every plugin ships against a contract version and is refused if incompatible; (4) cross-cutting concerns (authN/Z, audit, tenancy, tracing) are middleware/aspects, not copied into each module.

---

## 6. Security & compliance posture (cross-cutting, all phases)

Designed toward **all four bars simultaneously (D6)**. Detailed control→requirement mapping lives in `GOVERNANCE.md` and is surfaced in-product on the **Regulatory Governance page**.

- **Identity & access:** OIDC/SAML SSO, SCIM provisioning, RBAC + ABAC (tenant/workspace/role scoped), short-lived tokens, service-to-service mTLS (zero-trust).
- **Encryption:** TLS 1.3 in transit; AES-256 at rest; **FIPS-validated crypto mode** for FedRAMP; envelope encryption via KMS/Vault; **per-subject data keys to enable crypto-shredding** (see GDPR right-to-delete over immutable stores).
- **Secrets:** never in config/code/images — referenced by name, resolved from Vault/KMS/cloud secret manager via `ISecretProvider`.
- **Audit:** immutable, tamper-evident audit log of all access and admin actions (`IAuditSink`), tenant-scoped, exportable — SOC 2 evidence.
- **Data lifecycle (GDPR/HIPAA):** content capture opt-in; field-level encryption for captured prompt/response content; configurable **retention, redaction, and residency** per tenant/region; right-to-delete via crypto-shredding on ClickHouse partitions; BAA-ready PHI handling.
- **Air-gap (FedRAMP):** fully offline operation — no outbound calls on any critical path; pricing catalog imported as a **signed offline bundle**; all telemetry stays in-cluster.
- **Platform self-observability:** the platform dogfoods its own OTel; health/SLO dashboards; supply-chain security (SBOM, signed images, dependency scanning) in CI.

---

## 7. Ease-of-integration surface (what "minutes to onboard" means)

- **OTLP endpoint** — any OTel-instrumented app/gateway/framework points its exporter at us; zero product-specific code.
- **OpenAI-compatible proxy** — change a base URL; capture wire traffic + cost with no instrumentation.
- **Usage-event API** — CloudEvents 1.0 for coarse/custom events (RPA units, seats).
- **Adapter SDK** — documented plugin interface (`IUsageAdapter`) so a new closed surface is a small plugin, not a core change.
- **MCP server face** — agents query their own spend live via MCP tools/resources.
- Thin SDKs wrap the above per language as convenience, but the raw endpoints are always sufficient.

---

## 8. Service-level objectives (initial targets — refine in Phase 1)

- Ingestion accepts and durably enqueues an event in **< 10 ms p99** on the hot path (pricing/reconciliation happen async).
- No event loss under broker backpressure (at-least-once + idempotent dedup by event id).
- Cost estimate available **< 1 s** after ingestion; reconciled cost within provider billing lag (hours–1 day).
- Query API p95 **< 500 ms** for standard dashboard queries over 30-day windows.
- Horizontal scale: linear throughput with added ingestion/receiver replicas.

---

## 9. Decision Log

| Date | Decision | Rationale | By |
|------|----------|-----------|----|
| 2026-08-04 | D1–D6 locked (see §2) | Product-owner direction: enterprise-grade, security & efficiency & integration paramount | Owner |
| 2026-08-04 | Compliance = all four + in-product Regulatory Governance page | Owner explicitly requested every bar and a dedicated governance page | Owner |
| 2026-08-04 | `ARCHITECTURE.md` adopted as design substrate; this file + `DEVELOPMENT_PLAN.md` created | Kickoff of build planning | — |
| 2026-08-04 | **B1** design-system brand posture = **fully neutral/generic** base; SS&C is one example theme, not the foundation | Owner: product must look vendor-independent out of the box | Owner |
| 2026-08-04 | **B2** default chart palette = **generic accessibility-validated template**, tenant-swappable (each swap re-validated by the CVD gate) | Owner: a generic palette template changeable per brand | Owner |
| 2026-08-04 | Centralized **Design System** authored at `design-system/` (three-tier tokens, light/dark, white-label, palette validator, living styleguide) | Owner asked for a central styling authority so changes apply to all features | — |
| 2026-08-04 | **.NET 10 SDK installed** at `~/.dotnet` (10.0.302); Docker still absent | Owner chose "install .NET, build for real" when told the locked stack couldn't compile on this box | Owner |
| 2026-08-04 | **Vertical slice built** (contracts + normalizer + cost engine + ingest API + in-memory store + 15 passing tests); solution uses `.slnx` (.NET 10 default) | Execute the plan's first runnable increment; verify keystones against real code | — |
| 2026-08-04 | Storage slice = **in-memory `IEventStore`**; tenant = request header | Docker/ClickHouse unavailable on the dev box — the seam is proven with a fake, swappable at the composition root. Real backends = Phase 1 | — |
| 2026-08-04 | **B3 — lead with a zero-infra downloadable `.exe`** (embedded SQLite `solo` profile + self-contained single-file publish); server backends become an opt-in scale tier | Owner: "I want it as easy to integrate as possible, so it can be a downloadable .exe." Reframes reach without discarding D1 (distributed stays for scale). See ADR-0009 | Owner |
| 2026-08-04 | Deployment **profiles** (`solo`/`ephemeral`/`standard`/`distributed`) select the backend by config at the composition root | One product, many machines; backend swap is a one-line/config change behind `IEventStore` etc. | — |
| 2026-08-04 | Progressive Deployment (★) threaded through `DEVELOPMENT_PLAN.md` as a standing principle (all phases + continuous workstream + Phase 8/10) | Owner asked to bake the `.exe`-first philosophy into the plan | Owner |
| 2026-08-04 | **Phase 2 core: OTLP/HTTP ingestion** (`/v1/traces`, real envelope) + `ISpanMapper` (2 dialects) + async `IIngestChannel`/consumer | Advance past infra-blocked Phase-1 distributed backends to fully-verifiable-here ingestion work | — |
| 2026-08-05 | **Git repo established** — local repo at `ai-usage-tracker/`, `main` seeded on `github.com/AJKaye/Ai-usage-tracker`, Phase-3 work on branch `phase-3-cost-engine` | Owner provided the repo; enables checkpoints, isolated branches, CI, and a reviewable PR (owner chose: seed main + PR for Phase 3) | Owner |
| 2026-08-05 | **Phase 3 cost engine** built in 6 verified increments (branch `phase-3-cost-engine`): rate snapshotting + recompute, per-granularity, composite-key/date-effective catalog, in-tier additive+modes, catalog sources + Tier-3 + signed bundle, wire flow. 76 tests green. Design workflow was abandoned (API 529 overload) and re-done in the main loop; 2 salvaged sub-area designs folded in. Design locked in `docs/phase-3-cost-engine-design.md` | Execute Phase 3 (fully verifiable here, no infra); ultracode workflow fan-out amplified transient API overload, so pivoted to main-loop incremental build | — |
| 2026-08-05 | **Phase 4 reconciliation** built in 3 verified increments (branch `phase-4-reconciliation`, stacked on Phase 3): `CostReconciler` + `IReconciliationStore`, `OpenAiBillingConnector` + `AnthropicBillingConnector` (injected HttpClient, `ISecretProvider` keys), `/v1/reconcile` endpoints. 87 tests green. Connectors optional (air-gap: estimate stands, flagged). ClickHouse materialization + Azure/GCP/AWS connectors are the infra-blocked/later remainder | Execute Phase 4 (fully verifiable here — canned HTTP, no network/Docker); reconciliation layer per ARCHITECTURE §8.3 step 2 | — |
| 2026-08-05 | **Phase 5 multi-archetype ingestion** built in 3 verified increments (branch `phase-5-multi-archetype`, stacked on Phase 4): `OpenAiCompatibleProxy` (`UsageTracker.Ingestion.Proxy`), CloudEvents `/v1/events` (`CloudEventParser`/`CloudEventMapper`), `UsageTracker.Adapters.Reference` (UiPath/Copilot/ClaudeCode adapters + `AdapterRunner`), RAG coverage over OTLP. 102 tests green. All four archetypes land canonically. Live proxy HTTP route + real adapter network calls are the additive remainder | Execute Phase 5 (fully verifiable here — injected HttpClient, no network); breadth per ARCHITECTURE §8.3 step 3 | — |
| 2026-08-05 | **Phase 6 FinOps** built in 3 verified increments (branch `phase-6-finops`, stacked on Phase 5): `UsageTracker.FinOps` (tag-free allocation, unit economics, FOCUS projection, efficiency, `InMemoryScoreSink`) + serving endpoints (`/v1/allocation`,`/unit-economics`,`/focus`,`/efficiency`,`/scores`). 113 tests green. Allocation sums to 100% with no tags (exit criterion proven). gRPC + load test deferred | Execute Phase 6 (fully verifiable here — pure compute + HTTP); serving layer per ARCHITECTURE §8.3 step 5 | — |
| 2026-08-05 | **Audit correction:** Phase 1 distributed backends downgraded `[~]`→`[ ]` contract-only (were mislabeled "authored"; no ClickHouse/PG/Kafka code exists, only contracts) | Adversarial phase-0–5 review caught the one doc over-claim; runtime was always honest (fail-fast) | — |

*(Append new rows; never rewrite history.)*

---

## 10. Working rules for future agents

1. **Read this file, then `DEVELOPMENT_PLAN.md`, then `ARCHITECTURE.md`** before doing anything. Confirm which phase is active (top of this file). For anything with a UI, also read `design-system/DESIGN_SYSTEM.md`.
2. **Respect module contracts (§5).** Add behind an interface; don't reach across boundaries or share databases directly. **Any UI styles against design tokens only** — never a raw hex/px in a component (the stylelint gate enforces this).
3. **Update the living docs in the same change:** advance "Current phase," append to the Decision Log, and check off plan deliverables when you complete them.
4. **Re-verify drifting facts before relying on them** — all dollar figures, OTel `gen_ai.*` attribute names (Development-stability), FOCUS versions, and provider response quirks. `ARCHITECTURE.md` §9 lists the known-volatile items.
5. **Security is not optional in any phase.** New endpoints get authN/Z, audit, tenancy scoping, and a threat-model note. New data fields get a retention/residency/PII classification.
6. **Dual-mode always (D3).** Anything you build must work air-gapped self-host *and* multi-tenant SaaS. If a feature can't, flag it and record the constraint.
7. **When in doubt about intent, ask the owner** rather than assume — especially on scope, compliance interpretation, or contract-breaking changes.

---

## 11. Glossary

- **Archetype** — one of the four ingestion mechanics (proxy / SDK-OTel / OTLP / billing-reconciliation).
- **Span kind** — the type of unit of work (llm, agent, tool, retriever, …).
- **Multi-bucket tokens** — input/output split further into reasoning, cache-read, cache-creation, audio, image.
- **Subset vs additive** — whether a provider's cache/reasoning token counts are already inside the parent total (subset) or excluded (additive). See §4.
- **Estimated vs reconciled cost** — token×rate estimate vs realized dollars from a provider billing API.
- **FOCUS** — FinOps Open Cost & Usage Specification; the cross-vendor billing schema (v1.2+ covers tokens/credits).
- **Tag-free allocation** — attributing shared-endpoint cost to teams/users from captured dimensions without requiring upstream tags.
- **Crypto-shredding** — satisfying right-to-delete on an append-only store by destroying the per-subject encryption key.
- **Air-gap** — deployment with no outbound network access; pricing catalog arrives as a signed offline bundle.
