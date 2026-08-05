# AI Usage Tracker

Enterprise-grade central repository that tracks AI usage across every consuming surface (direct model APIs, gateways, agent frameworks, MCP servers, RAG pipelines, IDE assistants, RPA bots) and computes **cost** and **efficiency** — reconciling *estimated* per-event cost against *authoritative* provider billing.

> **Read first:** [`PROJECT_CONTEXT.md`](./PROJECT_CONTEXT.md) (north-star + locked decisions) → [`DEVELOPMENT_PLAN.md`](./DEVELOPMENT_PLAN.md) (11-phase roadmap) → [`ARCHITECTURE.md`](./ARCHITECTURE.md) (the what/why). UI work also reads [`design-system/DESIGN_SYSTEM.md`](./design-system/DESIGN_SYSTEM.md).

## Download &amp; run (zero infrastructure)

The easiest way to run it: **one self-contained executable, nothing to install** — no .NET, no Docker, no database server, no admin. It runs in the **`solo`** profile, storing to an embedded SQLite file next to the exe.

**Double-click it and it opens like a desktop app:** in `solo` mode the exe starts its
local server and opens the dashboard in a **chromeless app-mode browser window** (no
tabs/address bar) at `http://127.0.0.1:5000`. It's still a server underneath — so on a
headless box (or when you pin `ASPNETCORE_URLS` / run a server profile) it stays
window-less. Opt out of the window with `USAGETRACKER__NO_WINDOW=1` and just browse to
the URL.

```powershell
# Windows — double-click, or:
usage-tracker.exe                      # → opens a window at http://127.0.0.1:5000; data in usage-tracker.db
usage-tracker.exe                      # (set USAGETRACKER__NO_WINDOW=1 to run headless)
```
```bash
# Linux / macOS
./usage-tracker
```

Then point any OTel-`gen_ai.*`-shaped event at it:

```bash
curl -X POST http://localhost:5000/v1/ingest -H 'Content-Type: application/json' -H 'X-Tenant-Id: demo' \
  -d '{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5",
       "gen_ai.usage.input_tokens":200,"gen_ai.usage.cache_read.input_tokens":600,
       "gen_ai.usage.cache_creation.input_tokens":200,"gen_ai.usage.output_tokens":500,"span_id":"demo-1"}'
curl http://localhost:5000/v1/summary -H 'X-Tenant-Id: demo'
```

**Deployment profiles** (`USAGETRACKER__PROFILE`) — the *same product*, backend chosen by config:

| Profile | Storage | Infra | For |
|---------|---------|-------|-----|
| **`solo`** (default) | embedded SQLite (`USAGETRACKER__DB`) | **none** | the downloadable exe; laptops, edge, air-gapped, pilots |
| `ephemeral` | in-memory | none | tests / throwaway |
| `standard` | Postgres | 1 server | *authored, backend WIP* |
| `distributed` | ClickHouse + Kafka + Postgres | cluster | SaaS / large self-host — *WIP* |

Build the exe yourself: `dotnet publish src/UsageTracker.Ingestion.Api -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` (swap `-r` for `linux-x64`, `osx-arm64`, `win-arm64`, `linux-arm64`). CI publishes all five on every push (`.github/workflows/ci.yml` → `publish-exe`).

**Verified on this dev box:** the published `usage-tracker.exe` (~101 MB, all-in) ran with no .NET/Docker present, ingested + normalized + costed an event, persisted to SQLite, and the data **survived a process restart**.

## Status — walking skeleton (Phase 0 + Phase 2 vertical slice)

A **real, compiling, tested, runnable** slice of the ingestion path is in place. It is deliberately a thin vertical cut through the architecture, not the finished product — but every line builds and every test passes on .NET 10.

**What works today (verified):**
- Canonical `Session → Trace → Span` model + the module **contracts** (`UsageTracker.Contracts`) — the modularity seam everything plugs into.
- **Token normalizer** — the subset-vs-additive keystone (OpenAI/Google treat cache as a subset of input; Anthropic/Bedrock add cache back). Golden-tested.
- **3-tier cost engine** (ingested-USD → price-map → unpriced fallback) over a date-stamped seed catalog, pricing each token bucket (base/cache-read/cache-creation/reasoning) at its own rate. Golden-tested against hand-computed values.
- **Ingestion API** (ASP.NET Core minimal API) accepting `gen_ai.*`-shaped events → normalize → cost → store, with query + summary endpoints and header-based tenant isolation.
- **In-memory event store** implementing the same `IEventStore` contract the production ClickHouse store will (proves the seam; no Docker needed).
- **15 tests green** (2 golden suites + end-to-end HTTP incl. tenant isolation).

**What is explicitly NOT built yet** (see `DEVELOPMENT_PLAN.md`): ClickHouse/Postgres/Kafka backends, the OTLP wire receiver, reconciliation/provider connectors, the proxy + adapter archetypes, allocation/FinOps, security/tenancy hardening (SSO/mTLS/FIPS/crypto-shred), the React SPA + Regulatory Governance page, and MCP. Storage currently = in-memory; tenant id currently = a header (real system: OIDC/mTLS + `ITenantResolver`).

## Layout

```
src/
  UsageTracker.Contracts/         interfaces + DTOs only (canonical model, IEventStore, ITokenNormalizer, ICostTier, IPriceCatalog)
  UsageTracker.Normalization/     subset/additive token normalizers + registry
  UsageTracker.Cost/              3-tier cost engine + price catalog (offline seed bundle)
  UsageTracker.Storage.InMemory/  IEventStore fake (swap for ClickHouse later)
  UsageTracker.Ingestion.Api/     ASP.NET Core ingest/query/summary endpoints
tests/UsageTracker.Tests/         golden suites (normalizer, cost) + end-to-end HTTP
design-system/                    centralized tokens/specs/validator/styleguide (see its own README)
.github/workflows/ci.yml          build + test + design-system gates
```

Every non-contract project references **only** `UsageTracker.Contracts`, never each other — the modularity rule from `PROJECT_CONTEXT.md` §5.

## Build & run

Requires the **.NET 10 SDK** (`dotnet --version` ≥ 10.0). On this dev machine it lives at `~/.dotnet`; prepend it to PATH if `dotnet` isn't found:

```powershell
$env:PATH = "$HOME\.dotnet;$env:PATH"     # only if dotnet isn't already on PATH
```

```bash
dotnet build AiUsageTracker.slnx            # compile all projects
dotnet test  AiUsageTracker.slnx            # run the 15 tests (should be all green)

# run the ingestion API
ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project src/UsageTracker.Ingestion.Api
```

### Try it (live)

```bash
# health
curl http://127.0.0.1:5199/health

# ingest an Anthropic event (additive family: cache is added back → input 1000)
curl -s -X POST http://127.0.0.1:5199/v1/ingest \
  -H 'Content-Type: application/json' -H 'X-Tenant-Id: demo' \
  -d '{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5",
       "gen_ai.usage.input_tokens":200,"gen_ai.usage.cache_read.input_tokens":600,
       "gen_ai.usage.cache_creation.input_tokens":200,"gen_ai.usage.output_tokens":500,
       "span_id":"demo-1"}'
# → usage.inputTokens = 1000, cost.tier = "PriceMap", cost.totalCost = 0.01505

# ingest an OpenAI event (subset family: cache is inside input → input stays 1000)
curl -s -X POST http://127.0.0.1:5199/v1/ingest \
  -H 'Content-Type: application/json' -H 'X-Tenant-Id: demo' \
  -d '{"gen_ai.provider.name":"openai","gen_ai.response.model":"gpt-5.6",
       "gen_ai.usage.input_tokens":1000,"gen_ai.usage.cache_read.input_tokens":600,
       "gen_ai.usage.output_tokens":400,"span_id":"demo-2"}'
# → usage.inputTokens = 1000, usage.uncachedInputTokens = 400

# tenant rollup
curl -s http://127.0.0.1:5199/v1/summary -H 'X-Tenant-Id: demo'
```

## Next step

Phase 1 in `DEVELOPMENT_PLAN.md`: stand up the real ClickHouse/Postgres/Kafka backends behind the existing `IEventStore`/`IRelationalStore`/`IStreamBus` contracts (requires Docker, absent on the current dev box), then the OTLP receiver in Phase 2. Because everything sits behind contracts, swapping the in-memory store for ClickHouse is a composition-root change, not a rewrite.
