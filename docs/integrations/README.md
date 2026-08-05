# Integration Guides

How to feed AI usage into the tracker from any surface. Ordered by effort — most
integrations are **config, not code** (point an OpenTelemetry exporter at the OTLP
endpoint). See `ARCHITECTURE.md` §7.2 for the underlying framework hooks.

## The four ways in (pick the highest-fidelity one your surface supports)

| Path | Endpoint | When | Effort |
|------|----------|------|--------|
| **OTLP** (deep traces) | `POST /v1/traces` | any OpenTelemetry `gen_ai.*` / OpenInference exporter | config only |
| **Proxy** (zero-instrumentation) | OpenAI-compatible base-URL swap | you can change the client base URL | config only |
| **Usage-event API** (coarse) | `POST /v1/events` (CloudEvents 1.0) | RPA units, seats, premium requests | a few lines |
| **Flat ingest / SDK** | `POST /v1/ingest` (or the TS/Python SDK) | custom / quick start | a few lines |

Everything is tenant-scoped: send `X-Tenant-Id` (dev/self-host) or a `Authorization: Bearer <key>` (SaaS — the key resolves the tenant).

---

## Agent frameworks (OTel — config only)

Each of these emits or maps to OpenTelemetry `gen_ai.*` spans; point its OTLP
exporter at `https://<tracker>/v1/traces`.

- **LangChain / LangSmith** — set `LANGSMITH_OTEL_ENABLED=true` and the OTLP endpoint env (`OTEL_EXPORTER_OTLP_ENDPOINT`); LangSmith maps/exports `gen_ai.*`. LangChain callbacks (`on_llm_end` → `LLMResult`, `AIMessage.usage_metadata`) carry the tokens.
- **OpenAI Agents SDK** — auto spans + `Usage` via `context_wrapper.usage`; add an OTel processor with `add_trace_processor()` pointed at the endpoint.
- **Claude Agent SDK** — `ResultMessage.usage` (input/output/cache_creation/cache_read) + `total_cost_usd`; export via OTel. Claude Code CLI: set `CLAUDE_CODE_ENABLE_TELEMETRY=1` + the OTLP endpoint.
- **CrewAI** — OTel-based telemetry on by default; `UsageMetrics` includes cached/reasoning tokens. Point the OTLP env at `/v1/traces`.
- **AutoGen** — emits GenAI conventions natively (`execute_tool`/`create_agent`/`invoke_agent` spans); set the OTLP endpoint env.
- **LlamaIndex** — OpenInference instrumentation (a second dialect the mapper handles); or `TokenCountingHandler` + the flat ingest.

Set once, globally:
```
OTEL_EXPORTER_OTLP_ENDPOINT=https://<tracker>
OTEL_EXPORTER_OTLP_HEADERS=x-tenant-id=<tenant>   # or Authorization=Bearer <key>
```

## RAG pipelines

Retriever / embedding / reranker spans arrive over the same OTLP path and map to the
9-kind taxonomy automatically (`gen_ai.operation.name` = `embeddings`/`retrieval`, or
OpenInference `openinference.span.kind`). No extra work beyond instrumenting the
pipeline with OTel.

## Coarse surfaces (RPA, seats) — usage-event API

Surfaces with no per-token signal (UiPath AI units, Copilot seats/premium requests)
post a CloudEvent:
```json
POST /v1/events
{ "specversion":"1.0", "type":"com.uipath.ai.units", "source":"orchestrator/robot-7",
  "id":"evt-1", "data":{ "provider":"uipath", "granularity":"credit",
                         "units_consumed":2, "unit_type":"ai_unit" } }
```
Priced via the per-unit (CoarseUnit) cost tier. Or use a pull **Adapter** — see the
Adapter SDK (`src/UsageTracker.Adapters.Reference`) for the `IUsageAdapter` contract;
a third party ships a plugin against the contract version, no core change.

## MCP — agents read their own spend

The tracker is itself an MCP server (`POST /mcp`, JSON-RPC 2.0). See
[`mcp-server.md`](./mcp-server.md).

## Quality scores — be the aggregator, not the judge

Attach externally-computed eval scores from any framework:
```json
POST /v1/scores
{ "target_id":"<span-id>", "name":"helpfulness", "numeric":0.92, "source":"ragas" }
```
Read them back at `GET /v1/spans/{id}/scores`. The tracker never owns the judge
(ARCHITECTURE.md §6.3).
