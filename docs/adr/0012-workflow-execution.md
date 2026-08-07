# ADR 0012 — Visual agent-workflow builder + execution (execution is a telemetry producer)

**Status:** Accepted (2026-08-07) · **Decider:** Product owner · **Extends:** ARCHITECTURE.md §3 (canonical model), §6 (FinOps) · **Amends:** D6 (air-gap) — see the D6 addendum below

## Context
Through Phase 11 the tracker was a pure **observer** — it ingested, costed, allocated,
budgeted, and served AI usage produced *elsewhere*. The owner asked to expand it into a
**low-code, drag-and-drop builder**: place "agents" that perform "skills" in an order, hand
off to other agents, each node with custom inputs/outputs — and have the tracker itself
**execute** the workflow (call LLMs/tools, pass outputs→inputs, drive handoffs, like
n8n/LangFlow) with a **live overlay** as it runs.

Execution collides with two things the product otherwise guarantees: the **air-gap default**
(D6: no outbound calls on critical paths) and **secrets never stored** in config/code/images.
So execution had to be opt-in and egress-gated, not on by default.

## Decision
1. **Execution is a new *producer* of the canonical telemetry the tracker already consumes.**
   Each executed node builds a canonical `Span` and enqueues it on the existing
   `IIngestChannel`; the background `IngestConsumer` costs it (`_cost.Cost(span)`) and stores
   it. Therefore **cost, allocation, budgets, efficiency, FOCUS, and anomaly detection apply to
   executed workflows for free** — no parallel data path.
   - **One run == one `TraceId`** (the run id). A run's telemetry is exactly
     `SpanQuery{TraceId=runId}`.
   - **A handoff == a real parent→child span** (`ParentSpanId` = the upstream node's span).
   - **Nodes bind to telemetry** by `Metadata["agent"]` + a new `Metadata["skill"]` convention
     (no change to the `Span` record or `SpanKind` enum — `agent` was already a convention).
2. **New model, behind contracts** (`UsageTracker.Contracts/Workflows.cs`): `WorkflowDefinition`
   / `WorkflowNode` / `WorkflowEdge` / `NodePort` (the custom IO), `WorkflowRun` / `NodeRunState`,
   `IWorkflowStore` / `IRunStore` (tenant-scoped, in-memory now — the established store pattern),
   and a pluggable `INodeExecutor` (dispatch by `WorkflowNodeType {Llm,Http,Agent,Transform}`).
3. **A pure runner** (`UsageTracker.Orchestration`): Kahn topological order (cycles rejected at
   save), edge input-mapping (explicit or name-match), poison-isolation per node, cancellation,
   and a **deterministic dry-run** that projects order + cost with NO network and **enqueues
   nothing** (a dry-run must never pollute real telemetry). Runs execute on a background queue
   (`WorkflowRunExecutorService`, mirroring `IngestConsumer`/`BudgetScanService`) so `POST /run`
   returns the run id fast (202) and the run is truly async + cancelable.
4. **Liveness = client-side polling** (~3s), reusing the SPA's `useApi` dep-bump idiom — the
   product has no streaming transport, and polling is enough for a metering workload and works
   air-gapped.
5. **Phasing.** *12a* (this ADR): the builder, model/stores, per-node IO, the runner, the live
   overlay, and **executor stubs** (Transform is fully real/network-free; Llm/Http/Agent emit
   deterministic costed spans without any network) + dry-run. *12b* (next): the **real**
   network-calling executors behind an opt-in `execution` profile.

### D6 addendum — opt-in execution egress
The default posture is **unchanged**: `solo`/`analytics`/`ephemeral` remain air-gapped and
fail closed. Real network execution is gated by a future opt-in `execution` profile that (a)
permits egress **only to a configured host allowlist** (a new `AllowlistEgressPolicy`
implementing the same `IEgressGuard`), and (b) resolves provider keys via `ISecretProvider`
**by name** (never from config/code/images). Any executor that makes an outbound call MUST
`AssertEgressAllowed` first. Absent the `execution` profile, the stub/transform executors run
fully offline. This is a bounded, deliberate escape hatch — not a reversal of D6.

## Consequences
- (+) The tracker becomes an **active orchestrator** while every cost/FinOps feature it already
  has applies to what it runs — the reuse is the whole point.
- (+) **Additive/non-breaking**: no change to `Span`/`SpanKind`; new project + contracts only.
  213 tests green under `-warnaserror`.
- (+) **Verifiable here**: the 12a stubs execute end-to-end with no network, so create → dry-run
  → run → live overlay → cost-in-summary is provable on the zero-infra exe today.
- (−) First third-party **UI** dependency (`@xyflow/react`); it ships raw-hex CSS that had to be
  re-skinned to role tokens (`reactflow-theme.css`). Bundle grew ~200 KB.
- (−) `IWorkflowStore`/`IRunStore` are in-memory (runs don't survive restart) — a durable
  backend is a later swap behind the same contract, like the other stores.
- (−) Real execution (12b) widens the outbound surface; contained by the allowlist + fail-closed
  default + by-name secrets.

## Guidance for future agents
Keep the runner + executors **pure/deterministic** where possible — that is what makes dry-run
and the tests trustworthy. A new executor MUST assert egress before any network call and resolve
secrets by name. Never enqueue in dry-run. Keep binding metadata (`agent`/`skill`/`workflow`/
`node`) stamped by the runner so the trace tree + node binding can't drift. New stores must stay
tenant-scoped and (when durable) satisfy the same `IWorkflowStore`/`IRunStore` contracts.
