# AI Usage Tracker — Architecture Report

**A standalone central repository that tracks AI usage across every platform and tool — direct model APIs, agent frameworks, MCP servers, RAG pipelines, AI gateways, IDE assistants, and RPA bots with embedded LLM steps — and computes cost and efficiency.**

> **Purpose of this document.** This is the design substrate for future agents building the tracker. It is grounded in a 2026-08 survey of the observability/FinOps landscape, the OpenTelemetry GenAI conventions, MCP, and the five major providers' usage/billing APIs. It is deliberately opinionated: it states *what to build and why*, not just what exists. Read the whole thing before writing code — the "gotchas" sections are load-bearing.
>
> **Confidence tags** carry through from research: **[High]** = ≥2 credible sources or a vendor primary doc; **[Medium]** = single strong source; **[Low]** = thin / inferred / could not re-confirm. Dollar figures and experimental-spec details drift — re-verify anything load-bearing against a live source before shipping.
>
> **Compiled 2026-08-04.**

---

## 0. The one-paragraph thesis

No existing product tracks *all* AI-consuming surfaces. Observability tools (Langfuse, LangSmith, Datadog, Phoenix) trace instrumented code deeply but miss closed surfaces and treat cost as an afterthought. Gateways (LiteLLM, Portkey, Cloudflare) see wire traffic with zero instrumentation but can't see internal chain/tool structure and label cost an "estimate." FinOps tools (CloudZero, Finout, OpenMeter) reconcile billing-grade dollars but at coarse grain. The opportunity — and the hard part — is to be the **reconciliation hub** that ingests from all four archetypes, normalizes to one schema (OpenTelemetry GenAI `gen_ai.*` + FOCUS cost columns), and reconciles *estimated* per-event cost against *authoritative* provider-billing cost. Everything below serves that thesis.

---

## 1. Scope: what "a platform or tool" means here

The tracker must model usage from surfaces with wildly different fidelity. Design for a **fidelity spectrum**, not a single event shape:

| Surface class | Examples | Native usage signal | Fidelity |
|---|---|---|---|
| Direct model APIs | OpenAI, Anthropic, Gemini/Vertex, Azure Foundry, Bedrock | per-response `usage` object (tokens, cache, reasoning) + billing/usage reporting APIs | **Highest** — real tokens + reconcilable dollars |
| AI gateways / proxies | LiteLLM, Portkey, Cloudflare, Kong, Envoy | normalized usage + often cost, on the wire | High — real tokens, estimated cost |
| Agent frameworks | LangChain/Graph, LlamaIndex, OpenAI Agents SDK, Claude Agent SDK, CrewAI, AutoGen | callback/trace hooks; `usage_metadata` on messages | High — real tokens + trace tree |
| MCP servers | any MCP tool/sampling server | **none native** — no usage field on `CallToolResult` or `CreateMessageResult` | Low — must derive from underlying model call |
| RAG pipelines | retriever + embedding + rerank + generate | OTel/OpenInference spans (`retrieval`, `embedding`, `reranker`) if instrumented | Medium/High if instrumented |
| IDE assistants | Cursor, GitHub Copilot, Claude Code | admin/metrics APIs — varies wildly (see §7) | Cursor/Claude Code: real tokens; Copilot: seats + acceptances only |
| RPA w/ embedded LLM | UiPath, Automation Anywhere | abstracted "AI units" / governance logs | Lowest — no per-token; coarse credits |

**Design consequence:** the core event schema must degrade gracefully. A UiPath "1 AI unit" event and an Anthropic response with a five-way token split must both land in the same table, with a `granularity` marker so the cost engine knows whether it's pricing tokens, credits, seats, or request-counts. Do **not** design token-first and bolt on the coarse cases — design for the union and treat rich token data as the best-case fill.

---

## 2. Architectural landscape — the four ingestion archetypes

Every product surveyed reduces to one or a hybrid of four ingestion mechanics. These are **complementary, not competing** — the tracker should offer all four. [High]

| Archetype | Who does it | Mechanism | Strengths | Weaknesses |
|---|---|---|---|---|
| **Proxy / Gateway** | Helicone (proxy), Portkey, LiteLLM (proxy), Cloudflare, Kong, Envoy | App points base URL at gateway; it sees every request/response on the wire | Language-agnostic; **zero app code**; central choke point for cost/keys/limits/cache; captures cost natively | +20–40ms hop; availability dependency in critical path; **wire-level only — blind to internal chain/tool/retriever tree** |
| **SDK / in-process instrumentation** | Langfuse, LangSmith, OpenLLMetry, Lunary, Datadog, Phoenix | SDK wraps client libs / decorators / callbacks; emits spans out-of-band (async) | **Full trace-tree fidelity** (agents, tools, retrievers, nesting); no critical-path latency; rich metadata | Per-language SDK coverage; code changes; only sees instrumented code |
| **OpenTelemetry (OTLP + GenAI conventions)** | OpenLLMetry, Langfuse, LangSmith, Phoenix/OpenInference, Datadog, Envoy, Kong, LiteLLM (all support) | Standardized `gen_ai.*` / OpenInference spans over OTLP | **Vendor-neutral, portable, no lock-in**; one instrumentation → many backends; the emerging lingua franca | GenAI conventions still **Development/experimental** — schema churn; content capture opt-in |
| **Billing-API / usage-event reconciliation** | OpenMeter (CloudEvents), CloudZero, Finout, Amberflo, FOCUS exports | Ingest normalized usage/billing events + pull provider billing APIs | **Billing-grade accuracy**; ties to invoices/entitlements; cross-vendor rollup incl. GPU/cloud spend | Coarser granularity; reconciliation lag (hours–1 day); not real-time trace-level |

**The reference architecture the mature stacks converge on:** a routing **gateway** for real-time cost/control + a vendor-neutral **OTel tracing layer** for deep agentic traces + a **metering/billing reconciler** for billing-grade truth. No single surveyed product spans all four — that gap is the tracker's differentiator. [High]

---

## 3. The canonical data model

### 3.1 The hierarchy every trace tool converges on

Despite different names, all trace-based tools use the same nested shape. Adopt it. [High]

```
Session / Thread              grouping key: session_id / thread_id / gen_ai.conversation.id
└── Trace                     one end-to-end request; unique trace_id
    └── Span (a.k.a. Observation / Run)   one unit of work; parent_span_id builds the tree
        ├── kind: llm/generation     the model call — carries tokens + cost
        ├── kind: agent
        ├── kind: tool               (MCP tool calls map here)
        ├── kind: chain/workflow/task
        ├── kind: retriever/embedding    (RAG)
        └── kind: reranker/guardrail/evaluator
```

Name mapping across products (so ingest adapters can normalize):

| Product | Trace unit | Span unit | Grouping | Span kinds |
|---|---|---|---|---|
| Langfuse | Trace | Observation (Generation/Span/Event) | Session | 3 |
| LangSmith | Trace | Run | Thread | (freeform) |
| Datadog | Trace | Span | Session | 7 (llm/workflow/agent/tool/task/embedding/retrieval) |
| Phoenix/OpenInference | Trace | Span | — | **9** (+ reranker/guardrail/evaluator) |
| Lunary | Trace | Run | Thread | llm/agent/tool/chain/chat |
| OTel GenAI | (trace) | Span `{operation} {model}` | `gen_ai.conversation.id` | via `operation.name` enum |
| Helicone / Cloudflare | — (flat) | request record | session / gateway | flat, no deep tree |

**Make span `kind` first-class and extensible** — the agentic era needs agent/tool/retriever/guardrail/evaluator kinds, and Phoenix's 9-kind set is the most forward-looking superset. [High]

### 3.2 Canonical span field set (superset schema)

The union across all products. Store this; let adapters fill what their source provides.

- **Identity / tree:** `trace_id`, `span_id`, `parent_span_id`, `session_id`, `kind`, `name`, `status`
- **Model:** `request_model` + `response_model`, `provider`, `ml_app`/app-id, invocation params (`temperature`, `top_p`, `top_k`, `max_tokens`, `stream`, `seed`, `reasoning.level`/effort)
- **Content (opt-in, PII-sensitive):** `input_messages`, `output_messages`, `system_instructions`, `tool_definitions`
- **Usage (the critical multi-bucket set):** `input_tokens`, `output_tokens`, `total_tokens`, **`reasoning_output_tokens`**, **`cache_read_input_tokens`**, **`cache_creation_input_tokens`**, `audio_tokens`, `image_tokens` — *mutually-exclusive bucketing matters* so tokens aren't double-counted
- **Cost (fine-grained line items):** `input_cost`, `output_cost`, `total_cost` + `cache_read_cost`, `cache_creation_cost`, `reasoning_cost`, `tool_usage_cost`
- **Timing:** `start_time`, `end_time`, `duration`, **`time_to_first_token`** (TTFT)
- **Attribution / metadata:** `user_id`/`end_user`, `org_id`/`team_id`, `tags`, arbitrary `metadata`, `environment`, `version`, plus FinOps dims (§6)
- **Gateway-only extras:** `cache_hit`/status, `retry` status, `fallback` status, `load_balance` status
- **Coarse-surface fields (for RPA/IDE):** `granularity` (`token`|`credit`|`seat`|`request`), `units_consumed`, `unit_type` (e.g. "ai_unit", "premium_request")

### 3.3 The wire schema: OpenTelemetry GenAI `gen_ai.*` + OpenInference

**Adopt OTel GenAI semantic conventions as the primary wire schema.** It is the emerging cross-platform standard, emitted natively or ingestibly by AutoGen, Envoy, Kong, LiteLLM, LangSmith, Langfuse, Datadog, OpenLLMetry. [High]

**Structural facts:**
- The conventions now live in a **dedicated repo**: `open-telemetry/semantic-conventions-genai` (moved out of the main semconv repo). It covers spans, metrics, events, MCP, and provider-specific conventions (OpenAI, Anthropic, Azure AI Inference, Bedrock). [High]
- **Stability: essentially everything `gen_ai.*` is "Development"** (was "Experimental"). Only general-purpose attrs (`error.type`, `server.address`) are Stable. **Pin a version and plan for churn.** [High]

**Core span attributes** (verbatim names — this is a schema, get them exact): [High]

| Attribute | Meaning |
|---|---|
| `gen_ai.operation.name` | Required. Enum: `chat`, `text_completion`, `generate_content`, `embeddings`, `retrieval`, `execute_tool`, `invoke_agent`, `invoke_workflow`, `plan`, `create_agent`, `fetch_response`, `create_memory`, `search_memory`, `update_memory`, `delete_memory`, `upsert_memory`, `create_memory_store`, `delete_memory_store` |
| `gen_ai.provider.name` | Required. Replaces older `gen_ai.system`. Enum: `openai`, `anthropic`, `azure.ai.openai`, `azure.ai.inference`, `aws.bedrock`, `cohere`, `deepseek`, `gcp.gemini`, `gcp.gen_ai`, `gcp.vertex_ai`, `groq`, `ibm.watsonx.ai`, `mistral_ai`, `moonshot_ai`, `perplexity`, `x_ai` |
| `gen_ai.request.model` / `gen_ai.response.model` | requested vs responding model |
| `gen_ai.usage.input_tokens` / `.output_tokens` | prompt / completion tokens |
| `gen_ai.usage.cache_creation.input_tokens` / `.cache_read.input_tokens` | cache write / read |
| `gen_ai.usage.reasoning.output_tokens` | reasoning tokens |
| `gen_ai.request.{temperature,top_p,top_k,max_tokens,seed,stream,frequency_penalty,presence_penalty}` | request params |
| `gen_ai.request.reasoning.level` | effort level (e.g. Anthropic `output_config.effort`) |
| `gen_ai.response.id` / `.finish_reasons` / `.time_to_first_chunk` | response metadata + TTFT |
| `gen_ai.conversation.id` | session/thread id |
| `gen_ai.output.type` | `text` / `json` / `image` / `speech` |

**Agent & tool spans** (the agentic additions): [High]
- Agent: `gen_ai.agent.id`, `gen_ai.agent.name`, `gen_ai.agent.description`, `gen_ai.agent.version`
- Tool: `gen_ai.tool.name` (Required), `gen_ai.tool.call.id`, `gen_ai.tool.type` (`function`/`web_search`/`code_interpreter`), `gen_ai.tool.description`, opt-in `gen_ai.tool.call.arguments`/`.result`
- Span names: `create_agent {name}`, `invoke_agent {name}`, `execute_tool {tool.name}`, `plan {name}`

**Metrics:** `gen_ai.client.token.usage` (histogram, `{token}`, with `gen_ai.token.type` = `input`/`output`), `gen_ai.client.operation.duration`, `.time_to_first_chunk`, `.time_per_output_chunk`, `gen_ai.server.{request.duration,time_per_output_token,time_to_first_token}`. [High]

**Content events** (opt-in, PII-flagged): `gen_ai.client.inference.operation.details`, plus `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`, `gen_ai.evaluation.result`. [High]

**OpenInference** (Phoenix's parallel convention) is worth supporting as a second wire dialect — it bakes **explicit USD cost attributes** into spans (`llm.cost.prompt/.completion/.total` + `prompt_details.cache_read/.cache_write`, `completion_details.reasoning/.audio`), which `gen_ai.*` does not. Its 9 `openinference.span.kind` values (LLM/CHAIN/TOOL/RETRIEVER/EMBEDDING/AGENT/RERANKER/GUARDRAIL/EVALUATOR) are the richest kind taxonomy. [High]

### 3.4 Token-field normalization (a required adapter)

Sources disagree on token field names and semantics. The adapter layer must normalize all of these to the canonical set in §3.2: [High]

- **Name split:** `input_tokens`/`output_tokens` (OTel, OpenAI Responses, Anthropic, LangChain, Claude SDK, AutoGen mapping) **vs** `prompt_tokens`/`completion_tokens` (OpenAI Chat Completions, CrewAI, AutoGen `RequestUsage`).
- **Subset vs additive — THE double-counting trap:** On OpenAI and Google, `cached_tokens`/`reasoning_tokens`/`thoughtsTokenCount` are **subsets already inside** the parent input/output total — never add them on top. On **Anthropic and Bedrock**, `input_tokens` is the **uncached remainder** — true prompt = `input_tokens + cache_read_input_tokens + cache_creation_input_tokens`, and you **must add them back**. Get this backwards and you mis-bill every cached request. The OTel Anthropic convention encodes exactly this normalization rule.
- **Reasoning/thinking tokens bill as output** on every provider and are invisible in the response text — visible output length ≠ billed output.

---

## 4. The cost engine

### 4.1 The 3-tier fallback (industry-standard recipe)

Langfuse documents it most explicitly; LiteLLM/Phoenix/Datadog implement variants. [High]

1. **Use directly-ingested USD cost** if the source provides it (gateway or provider returned cost). Most accurate.
2. **Else: token counts × per-model price map.** The price map is the core asset.
3. **Else: tokenize the text yourself** (tiktoken `o200k_base`/`cl100k_base`, Claude tokenizer) then price.

**Two hard rules:**
- **Snapshot the price at ingestion time** so historical costs stay stable when prices change. Store the rate used, not just the cost. [High]
- **Separate *estimated* per-event cost from *reconciled* billing cost as two distinct layers.** Gateways (Cloudflare explicitly) label token-derived cost an estimate; FinOps tools reconcile against provider billing APIs. The tracker should show both and surface the delta. [High]

### 4.2 The price catalog — the core maintained asset

- **De-facto public price map:** LiteLLM's `model_prices_and_context_window.json` (`github.com/BerriAI/litellm`) — per model: `input_cost_per_token`, `output_cost_per_token`, `cache_creation_input_token_cost`, `cache_read_input_token_cost`, `output_cost_per_reasoning_token`, capability flags, `mode`. **Fork/track it as the seed catalog.** [High]
- **Key the catalog on far more than `(model, tokens)`** — it must be a composite key: `(model × token-category × service_tier × context-tier × modality × batch/fast/geo flags × deployment-type × region)`. See §5 gotchas.
- **Date-stamp and version every rate** — introductory prices (e.g. Anthropic Sonnet 5 intro $2/$10 through 2026-08-31), promo windows, new cache-write charges. Historical recompute needs the rate *in effect at the time*. [High]

### 4.3 Where pricing data actually comes from

The single biggest maintenance asymmetry in the whole system: [High]

| Provider | Machine-readable pricing API? | Realized-cost API returns $? |
|---|---|---|
| **OpenAI** | ❌ No — human page + `.md`/`llms.txt` only | ✅ Costs API (daily) |
| **Anthropic** | ❌ No — page only | ✅ cost_report (daily; **excludes Priority Tier**) |
| **Google** | ✅ **Cloud Billing Catalog API** (`cloudbilling.googleapis.com/v1/services/{id}/skus` — tiered rates, regions, `pricingExpression.tieredRates[]`) | ✅ BigQuery billing export (~1-day lag) |
| **Azure** | ✅ **Retail Prices API** (`prices.azure.com/api/retail/prices` — public, unauth, joins on `meterId`) | ✅ Cost Management Query API |
| **AWS Bedrock** | ✅ **Price List API** (`GetProducts`, ServiceCode `AmazonBedrock`; bulk files, no auth) | ✅ Cost Explorer / CUR 2.0 |

**Implication:** the three clouds let you auto-sync an effective-dated, per-region, per-SKU rate table programmatically — a real advantage; wire those up. OpenAI and Anthropic have **no pricing API**, so their catalog must be hand-curated/scraped and date-stamped. For all five, use the realized-cost API as reconciliation truth and the response `usage` for live per-request attribution.

---

## 5. Cost-engine gotchas (the load-bearing list)

Every one of these breaks naive `tokens × rate`. A serious tracker handles all of them. [High unless noted]

1. **Subset vs additive token decomposition** (see §3.4) — the #1 mispricing bug.
2. **One request has multiple input rates simultaneously** — base, cache-read (~0.1×), cache-write (1.25× for 5m / 2× for 1h on Anthropic; free on some models, charged on newer like GPT-5.6 and Azure gpt-5.6). Model each token category as its own priced line item.
3. **Reasoning/thinking tokens bill at output rate** on every provider.
4. **Batch API = ~50% off everywhere** — but with feature restrictions (no caching/tools in batch on Bedrock/Google). Key the rate on the batch flag/service tier.
5. **Tiered / context-length pricing applies to the *whole request*** once the prompt crosses a threshold (Google Pro >200K, Bedrock Sonnet 1M >200K, OpenAI long-context column) — not just the tokens above it.
6. **Modality matters** — audio/image/video priced differently within one model, sometimes per-minute/per-second/per-image, not per token. Use the per-modality breakdowns (`promptTokensDetails[]` etc.).
7. **Per-call tool surcharges stack on top of tokens** — web search (~$10/1k calls), file search, code interpreter/execution (per session or per hour). Some errored searches are free.
8. **Non-token billing regimes coexist** — Azure PTU and AWS Provisioned Throughput bill **per hour regardless of tokens**; fine-tuned model hosting is hourly; reservations amortize. `tokens × rate` is meaningless here — the catalog needs a `pricing_mode` (`per_token` | `per_hour` | `per_unit` | `per_request`).
9. **Deployment-type / region / service-tier / data-residency multipliers** — same model + same tokens bills differently: Azure Standard vs GlobalStandard vs DataZone; AWS per-region; Anthropic `inference_geo:"us"` = 1.1×; +10% data-residency uplifts. Anthropic Managed Agents add **$0.08/session-hour**; Claude fast mode is flat $10/$50 per MTok.
10. **Tokenizer changes shift cost at constant rates** — Claude 4.7+ produces **~30% more tokens** for the same text. Re-baseline per model.
11. **Third-party model pricing is set by the cloud, not the vendor** (Bedrock, Foundry) and varies by region — never reuse first-party list prices for a cloud-hosted model.
12. **Reporting APIs return dollars but lag and aggregate** (OpenAI/Anthropic Costs = daily; AWS CUR = per-usage-type-per-day; Google BigQuery ~1 day). Per-request cost attribution needs invocation logs (Bedrock model-invocation logging) or your own `usage`-based estimate reconciled against the billing feed. **Anthropic's cost_report excludes Priority Tier** — track it via the usage endpoint.
13. **Provider-specific response quirks** — Azure injects `content_filter_results`/`prompt_filter_results`, uses `finish_reason: content_filter`, and returns **HTTP 400 on a filtered prompt**; Anthropic splits streaming usage across `message_start` (partial/null) and `message_delta` (final output tokens) — merge both. OpenAI Chat Completions vs Responses, and Bedrock Converse vs InvokeModel, each expose **two different usage schemas**.
14. **Date-effective pricing** — historical recompute must use the rate in effect at event time. Version and date-stamp the catalog (see §4.2).
15. **Coarse surfaces don't speak tokens** — UiPath = abstract "AI Units" (GenAI activity = 1–2 units); Copilot = premium requests × per-model multiplier ($0.04/request overage); only Cursor and Claude Code expose real tokens + spend. The cost engine needs a per-`granularity` pricing path, not a token-only path.

---

## 6. FinOps: cost allocation, attribution & efficiency

### 6.1 Adopt FOCUS for the cost/billing layer

**FOCUS (FinOps Open Cost & Usage Specification)** normalizes billing across vendors and — as of **v1.2 (2025-05-29)** — added virtual-currency lifecycle tracking for **tokens and credits**, the concrete entry point for AI billing (latest v1.4, 2026-06-04). AWS/Azure/GCP/OCI all publish FOCUS exports. [High; exact dates Medium]

Adopt these FOCUS column names for the reconciled-cost layer:
- **`ConsumedQuantity` / `ConsumedUnit`** — e.g. tokens, AI-units
- **`PricingQuantity` / `PricingUnit`**
- **`BilledCost` / `EffectiveCost` / `ListCost` / `ContractedCost`**
- **`ListUnitPrice` / `ContractedUnitPrice`**, `SkuPriceId`

### 6.2 Allocation dimensions & tag-free attribution

- FinOps allocation taxonomy: **Project, Environment, Workload, Team, Cost Center, Usage Type**. AI's hard part is "identifying the consumer of the model output." Showback-first is recommended for AI. [High]
- **Tag-free / dimension-based allocation is the frontier** (CloudZero, Finout): allocate 100% of a shared endpoint's cost to teams/features/customers/users *without* requiring upstream tags — critical for the agentic era where many agents share one model endpoint. Build allocation to work from captured span dimensions (user_id, agent.id, session, MCP session) even when the caller didn't tag. [High]

### 6.3 Efficiency metrics — two distinct layers

**Operational efficiency** (cheap, derived from spans automatically — near-universal): [High]
- Latency (span duration), **TTFT**, tokens/sec, token counts, error/success rate, throughput
- **Cache hit rate + cost/time saved** (gateway strength)
- **Reliability signals** — retry counts, fallback activations, load-balancing (gateway-exclusive telemetry from Portkey/LiteLLM)

**Unit economics** (the FinOps efficiency story): [High]
- Resource efficiency: **cost per token, cost per inference, cost per API call**
- Business/outcome: **cost per assist, cost per agent action, cost per case deflected, cost per outcome** — this is the metric that matters in the agentic era and the one no gateway computes for you.

**Quality** (requires an evaluator — a separate layer): [High]
- Model quality as a generic **"score"** object (name + value: numeric/categorical/boolean) attached to a span/trace, from four sources: **LLM-as-judge**, **code/deterministic evaluators**, **human annotation queues**, **end-user feedback** (thumbs/comments).
- **Recommended positioning: be the score *aggregator*, not the eval engine** (Helicone's stance). Ingest externally-computed scores from any framework via a `POST .../score` endpoint; don't own the judge. This keeps the tracker framework-agnostic and avoids competing with the eval platforms.

---

## 7. Ingesting from closed / coarse surfaces (adapters, not proxies)

These surfaces need dedicated pull-adapters. Fidelity varies enormously. [High unless noted]

| Surface | Mechanism | Token-level? | Key endpoint / signal |
|---|---|---|---|
| **Cursor** | Admin API (HTTP Basic, key as username), base `api.cursor.com` | ✅ **Yes** | `POST /teams/filtered-usage-events` → per-event `model`, `tokenUsage{inputTokens, outputTokens, cacheWriteTokens, cacheReadTokens, totalCents}`, `chargedCents`, `isTokenBasedCall`, `conversationId`, `maxMode`; `POST /teams/spend`; `POST /teams/daily-usage-data` |
| **Claude Code** | (a) OTel client-side: `CLAUDE_CODE_ENABLE_TELEMETRY=1` → metric `claude_code.token.usage` (attrs `type`=input/output/cacheRead/cacheCreation, `model`, `query_source`), `claude_code.cost.usage` (USD). (b) Anthropic Admin API server-side (authoritative) | ✅ **Yes (both)** | `GET /v1/organizations/usage_report/messages`; `GET /v1/organizations/cost_report` |
| **GitHub Copilot** | Metrics API (transitioning to report-download model, API version `2026-03-10`) + billing/seats | ❌ **No tokens anywhere** | `GET /orgs/{org}/copilot/metrics` (28-day, engagement only: suggestions/acceptances/chats); `GET /orgs/{org}/copilot/billing/seats` → `last_activity_at`, `last_activity_editor`. Cost lever = **premium requests** (per-prompt × per-model multiplier, $0.04/req overage) |
| **UiPath** | AI Units via licensing + Insights dashboards (AI Trust Layer gateway meters embedded LLM work) | ❌ **No** (abstracted to AI units) | GenAI activity = 1 unit (2 w/ Context Grounding); Automation Cloud → Admin → Licenses / Insights "AI units consumption". No confirmed per-token REST API |
| **Automation Anywhere** | AI Governance dashboard + AI prompt log + audit/event logs | Partial/unconfirmed [Low] | AI Governance; likely via Control Room APIs / audit-log export |

**Design consequence:** for token-less surfaces (Copilot, UiPath, AA), the tracker records coarse `units_consumed` events with `granularity` set accordingly and prices them via the per-granularity path (§5.15). Don't force these into the token model.

### 7.1 MCP — an interception point, not a usage source

MCP carries **no native usage/token/cost field** — `CallToolResult` has only `content`/`structuredContent`/`isError`/`_meta`, and `CreateMessageResult` (sampling) returns `model`/`stopReason`/`role`/`content` but **no `usage`**. [High] So:
- **Derive** token/cost from the underlying model call (the LLM span behind the tool), and **correlate** via `mcp.session.id` + W3C trace context propagated in `params._meta`.
- **OTel MCP conventions exist** (same GenAI repo): `mcp.method.name` (e.g. `tools/call`), `mcp.session.id`, `mcp.resource.uri`, `mcp.protocol.version`; metrics `mcp.client/server.operation.duration`, `.session.duration`. MCP tool-call spans map cleanly to GenAI `execute_tool` spans. [High]
- **Two roles for the tracker w.r.t. MCP:**
  1. **As an MCP server** — expose usage queries as MCP *tools* (with `outputSchema` → `structuredContent`) and standing datasets as *resources* (with `resources/subscribe` + `notifications/resources/updated` for push). Lets agents query their own spend live.
  2. **As an MCP proxy/interceptor** — MCP is transport-agnostic JSON-RPC 2.0, so a gateway can observe all traffic and annotate `_meta`. Docker MCP Gateway (`docker/mcp-gateway`) is the first-party example with built-in call tracing. [High]

### 7.2 Agent-framework hooks

Wire adapters into each framework's native telemetry rather than re-instrumenting: [High]
- **LangChain/LangSmith:** `BaseCallbackHandler` (`on_llm_end` → `LLMResult`), `AIMessage.usage_metadata`; LangSmith ingests/maps `gen_ai.*` and exports OTel (`LANGSMITH_OTEL_ENABLED=true`).
- **OpenAI Agents SDK:** auto spans + `Usage` via `context_wrapper.usage`; proprietary exporter, OTel only via `add_trace_processor()`.
- **Claude Agent SDK:** `ResultMessage.usage` (input/output/cache_creation/cache_read), `total_cost_usd` (client estimate), `model_usage` (whole-tree). Claude Code CLI has separate OTel (§7).
- **CrewAI:** `UsageMetrics` (incl. `cached_prompt_tokens`, `reasoning_tokens`, `cache_creation_tokens`); OTel-based, telemetry on by default (opt out `CREWAI_DISABLE_TELEMETRY=true`).
- **AutoGen:** emits GenAI conventions natively (`execute_tool`/`create_agent`/`invoke_agent` spans); `RequestUsage` (prompt/completion tokens).
- **LlamaIndex:** `TokenCountingHandler` (+ embedding token counts), `Dispatcher`/`BaseEventHandler`; OpenInference instrumentation.

---

## 8. Recommended architecture for the tracker

### 8.1 Component diagram

```
┌─────────────────────── INGESTION (all four archetypes) ───────────────────────┐
│  OTLP receiver          Proxy/gateway mode      Usage-event API      Pull adapters │
│  (gen_ai.* +            (optional inline;       (CloudEvents 1.0     (billing APIs, │
│   OpenInference)        OpenAI-compat)          for coarse events)   Cursor, Copilot,│
│                                                                       UiPath, clouds)│
└───────────────┬───────────────┬──────────────────┬───────────────────┬────────────┘
                │               │                  │                   │
                ▼               ▼                  ▼                   ▼
        ┌──────────────────────────────────────────────────────────────────┐
        │  NORMALIZATION LAYER                                               │
        │  • token-field normalizer (subset-vs-additive, name aliases)      │
        │  • schema mapper → canonical Session→Trace→Span (§3.2)            │
        │  • granularity tagger (token|credit|seat|request)                 │
        └───────────────────────────────┬──────────────────────────────────┘
                                         ▼
        ┌──────────────────────────────────────────────────────────────────┐
        │  COST ENGINE (3-tier: ingested → price-map → tokenize)            │
        │  • composite-key price catalog (fork LiteLLM json), date-stamped   │
        │  • per-granularity pricing paths                                   │
        │  • estimated-cost layer  ←────reconcile────→  billing-truth layer  │
        └───────────────────────────────┬──────────────────────────────────┘
                                         ▼
        ┌──────────────────────────────────────────────────────────────────┐
        │  STORAGE:  events/spans → ClickHouse (analytics)                   │
        │            catalog/allocation/billing state → Postgres             │
        │            ingest stream → Kafka (optional, high volume)           │
        └───────────────────────────────┬──────────────────────────────────┘
                                         ▼
        ┌──────────────────────────────────────────────────────────────────┐
        │  SERVING:  query API · FOCUS-column cost views · unit-economics    │
        │            (cost/outcome) · score aggregation · MCP server face    │
        │            · allocation (tag-free, dimension-based) · dashboards   │
        └──────────────────────────────────────────────────────────────────┘
```

Storage choice mirrors the field convergence: **ClickHouse** for high-volume event/trace analytics, **Postgres** for relational catalog/billing/entitlement state, **Kafka** for ingest streaming at volume. [High — this is the pattern OpenMeter and Langfuse-class tools use]

### 8.2 Six design principles (the distilled takeaways)

1. **Adopt OTel GenAI + OpenInference as the wire schema**, pin a version, plan for churn (Development-stage). OTLP is the primary neutral ingest path.
2. **Offer all four ingestion modes** — OTLP (fidelity), proxy (zero-instrumentation breadth), usage-event/CloudEvents API (coarse + billing reconciliation), provider-billing-API pull (ground truth). Spanning all four is the differentiator.
3. **Model the canonical Session→Trace→Span tree** (§3.2) with span `kind` first-class and extensible to agent/tool/retriever/guardrail/evaluator.
4. **Build cost as a 3-tier engine** over a composite-key, date-stamped, multi-bucket price map; keep **estimated cost and reconciled billing cost as separate layers** and surface the delta.
5. **Make "score" a generic attachable object** and stay framework-agnostic on evals; capture operational efficiency (latency/TTFT/cache/retry/fallback) automatically.
6. **Tag-free, dimension-based allocation** for shared endpoints; unit economics up to **cost per outcome**.

### 8.3 Suggested build order (MVP → full)

1. **MVP:** OTLP receiver (`gen_ai.*`) + canonical span store (ClickHouse) + 3-tier cost engine seeded from LiteLLM's price JSON. Covers every OTel-emitting gateway/framework on day one.
2. **Provider reconciliation:** pull adapters for the three clouds' pricing APIs (Google Catalog, Azure Retail, AWS Price List) + realized-cost APIs (OpenAI Costs, Anthropic cost_report). Wire the estimated-vs-reconciled delta.
3. **Coarse surfaces:** Cursor + Claude Code adapters (real tokens), then Copilot/UiPath (units/seats) with the per-granularity pricing path.
4. **MCP face:** expose the tracker as an MCP server (usage-query tools + resources) so agents can read their own spend.
5. **FinOps layer:** FOCUS-column cost views, tag-free allocation, cost-per-outcome unit economics.
6. **Score aggregation:** generic score-ingest endpoint; framework-agnostic quality overlay.

---

## 9. Open items to verify before shipping (do not treat as settled)

Carried from the research, flagged Medium/Low — re-verify against live sources:
- **All dollar figures drift constantly** — pull live before displaying.
- OTel GenAI conventions are **Development-stability**; attribute names may change between versions.
- FOCUS exact version/date lineage (v1.1/v1.3 dates; whether model/provider are first-class tag keys) — Medium.
- OpenAI `usage` audio/prediction sub-fields (reference page is JS-gated) — Medium.
- Anthropic streaming `message_start` null-field partition is prose-documented only — Medium.
- Bedrock InvokeModel token headers (`X-Amzn-Bedrock-*`), `amazon-bedrock-invocationMetrics` field names, exact CUR usage-type strings, and `AmazonBedrock` service code — verify against live calls — Medium/Low.
- Azure current `serviceName` filter string for AOAI meters ('Azure OpenAI' vs 'Cognitive Services') — Medium.
- GitHub Copilot per-model premium-request multiplier table — not captured verbatim — Medium.
- UiPath / Automation Anywhere programmatic consumption endpoints (vs dashboard-only) — Low.
- Helicone OSS license & streaming token handling; Lunary license & self-host boundary — Low.
- WebSearch was unavailable during research; "High" ratings lean on multiple agreeing vendor docs, not independent third parties.

---

## 10. Reference index (primary sources)

- **OTel GenAI conventions:** `github.com/open-telemetry/semantic-conventions-genai` — spans, agent-spans, metrics, events, mcp, anthropic, and provider files.
- **Price map to fork:** `github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json`
- **FOCUS:** `focus.finops.org/what-is-focus/`, `/focus-columns/`
- **FinOps for AI:** `finops.org/wg/finops-for-ai-overview/`, `finops.org/framework/capabilities/unit-economics/`
- **Cloud pricing APIs:** Google `cloudbilling.googleapis.com/v1/services/{id}/skus`; Azure `prices.azure.com/api/retail/prices`; AWS Price List `GetProducts` (ServiceCode `AmazonBedrock`).
- **Realized-cost APIs:** OpenAI `/v1/organization/costs`; Anthropic `/v1/organizations/cost_report` + `/usage_report/messages`; Azure Cost Management Query API; GCP BigQuery billing export; AWS Cost Explorer / CUR 2.0.
- **Closed surfaces:** Cursor `api.cursor.com` Admin API; Claude Code `code.claude.com/docs/en/monitoring-usage`; GitHub `docs.github.com/en/rest/copilot`; UiPath `docs.uipath.com/.../ai-units`.
- **MCP:** `modelcontextprotocol.io/specification/2025-06-18`; `docker/mcp-gateway`.
- **Provider usage-object shapes:** each provider's Messages/Chat API reference; Anthropic `claude-api` skill (bundled) for Claude usage/pricing specifics.
