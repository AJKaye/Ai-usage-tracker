# AI Usage Tracker — TypeScript SDK

A thin, **zero-dependency** client over the tracker's HTTP API (uses global `fetch`,
Node 18+ / modern browsers). The raw `/v1` endpoints are always sufficient — this
just types the common onboarding actions.

```ts
import { UsageTracker } from './index'

const ut = new UsageTracker({ baseUrl: 'http://localhost:5000', tenant: 'acme' })

// 1. Ingest a model call's usage
await ut.ingest({ provider: 'anthropic', model: 'claude-opus-5', inputTokens: 1000, outputTokens: 500 })

// 2. Send a coarse usage event (e.g. an RPA "AI unit")
await ut.sendUsageEvent('com.uipath.ai.units', 'orchestrator/robot-7',
  { provider: 'uipath', granularity: 'credit', units_consumed: 2, unit_type: 'ai_unit' })

// 3. Attach an eval score (be the aggregator, not the judge)
await ut.postScore('span-123', 'helpfulness', 0.92, 'ragas')

// 4. Read the rolled-up spend
const s = await ut.summary()
console.log(`${s.spanCount} events, ${s.totalEstimatedCost} ${s.currency}`)
```

**Auth:** pass `tenant` for dev/self-host, or `apiKey` for authenticated/SaaS
deployments (the key resolves the tenant server-side; the header can't spoof it).

For deep tracing, point any OpenTelemetry `gen_ai.*` exporter at `POST /v1/traces`
instead — no SDK needed (see `docs/integrations/`).
