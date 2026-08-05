import { CodeBlock } from '../components/CodeBlock'

// The "how do I get my tools reporting into this?" page. One card per tracking
// method, each with copy/paste (and where useful download) snippets built from the
// LIVE origin + current tenant — so what you copy actually targets this instance.
// Only methods with a wired endpoint are shown as ready; deferred ones are flagged.
export function Integrations() {
  const origin = window.location.origin
  const tenant = localStorage.getItem('tenant') || 'default'

  return (
    <div className="grid" style={{ gap: 'var(--space-8)' }}>
      <p className="muted">
        Point your AI tools at this tracker — it's a telemetry sink, so tools <em>report in</em>.
        Snippets below target <code>{origin}</code> as tenant <code>{tenant}</code> (change the
        tenant in the top bar). Pick the highest-fidelity path your surface supports.
      </p>

      {/* 1. OTLP — deepest fidelity, config only */}
      <Card
        title="OpenTelemetry (agent frameworks, RAG, any OTel app)"
        badge="deepest fidelity · config only"
        blurb="Any OpenTelemetry gen_ai.* exporter — LangChain/LangSmith, OpenAI Agents SDK, Claude Agent SDK, CrewAI, AutoGen, LlamaIndex, or your own instrumented app. Set two env vars; no code."
      >
        <CodeBlock lang="bash" filename="usage-tracker.env" code={
`# Point any OpenTelemetry exporter at the tracker
OTEL_EXPORTER_OTLP_ENDPOINT=${origin}
OTEL_EXPORTER_OTLP_HEADERS=x-tenant-id=${tenant}
# framework-specific enables, e.g. LangSmith:
LANGSMITH_OTEL_ENABLED=true`
        } />
        <p className="muted small">Traces POST to <code>{origin}/v1/traces</code> (real OTLP <code>ExportTraceServiceRequest</code>).</p>
      </Card>

      {/* 2. CloudEvents — coarse surfaces (RPA units, seats) */}
      <Card
        title="Usage-event API (RPA units, seats, premium requests)"
        badge="coarse surfaces · CloudEvents 1.0"
        blurb="Surfaces with no per-token signal (UiPath AI units, Copilot seats/premium requests). POST a CloudEvent; priced via the per-unit cost tier."
      >
        <CodeBlock lang="bash" filename="send-usage-event.sh" code={
`curl -X POST ${origin}/v1/events \\
  -H "Content-Type: application/json" \\
  -H "X-Tenant-Id: ${tenant}" \\
  -d '{
    "specversion": "1.0",
    "type": "com.uipath.ai.units",
    "source": "orchestrator/robot-7",
    "id": "evt-001",
    "data": { "provider": "uipath", "granularity": "credit",
              "units_consumed": 2, "unit_type": "ai_unit" }
  }'`
        } />
      </Card>

      {/* 3. Direct ingest + SDKs */}
      <Card
        title="Direct ingest (custom code, quick start)"
        badge="a few lines · SDK or curl"
        blurb="Send one gen_ai.* usage event directly — good for custom tooling or a smoke test. Use the zero-dependency SDKs, or plain curl."
      >
        <CodeBlock lang="typescript" code={
`import { UsageTracker } from '@usage-tracker/sdk'  // sdk/typescript

const ut = new UsageTracker({ baseUrl: '${origin}', tenant: '${tenant}' })
await ut.ingest({ provider: 'anthropic', model: 'claude-opus-5',
                  inputTokens: 1000, outputTokens: 500 })`
        } />
        <CodeBlock lang="python" code={
`from usage_tracker import UsageTracker  # sdk/python (stdlib only)

ut = UsageTracker("${origin}", tenant="${tenant}")
ut.ingest(provider="anthropic", model="claude-opus-5",
          input_tokens=1000, output_tokens=500)`
        } />
        <CodeBlock lang="bash" filename="ingest.sh" code={
`curl -X POST ${origin}/v1/ingest \\
  -H "Content-Type: application/json" -H "X-Tenant-Id: ${tenant}" \\
  -d '{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5",
       "gen_ai.usage.input_tokens":1000,"gen_ai.usage.output_tokens":500,"kind":"llm"}'`
        } />
      </Card>

      {/* 4. MCP — agents read their own spend */}
      <Card
        title="MCP server (agents read their own spend)"
        badge="Model Context Protocol · JSON-RPC 2.0"
        blurb="Connect any MCP client to let an agent query its own usage/cost live. Tools: usage_summary, cost_by_provider, recent_spans."
      >
        <CodeBlock lang="json" filename="mcp-client-config.json" code={
`{
  "mcpServers": {
    "ai-usage-tracker": {
      "url": "${origin}/mcp",
      "headers": { "X-Tenant-Id": "${tenant}" }
    }
  }
}`
        } />
      </Card>

      {/* 5. Scores — be the aggregator */}
      <Card
        title="Quality scores (attach eval results)"
        badge="be the aggregator, not the judge"
        blurb="Attach an externally-computed eval score from any framework (ragas, LLM-as-judge, human review) to a span or trace."
      >
        <CodeBlock lang="bash" code={
`curl -X POST ${origin}/v1/scores \\
  -H "Content-Type: application/json" -H "X-Tenant-Id: ${tenant}" \\
  -d '{"target_id":"<span-id>","name":"helpfulness","numeric":0.92,"source":"ragas"}'`
        } />
      </Card>

      {/* 6. Adapters — honest: install a plugin, no UI wiring */}
      <Card
        title="Closed surfaces (Cursor, Copilot, UiPath) — adapters"
        badge="plugin · install, not click"
        blurb="Token/seat data from closed products is pulled by an adapter plugin (IUsageAdapter), loaded by path. Ship one against the contract version — no core change. There's no in-app install yet; drop the plugin next to the exe."
      >
        <p className="muted small">
          See <code>docs/integrations/</code> and the reference adapters under
          <code> src/UsageTracker.Adapters.Reference</code> (UiPath, Copilot, Claude Code) +
          the Cursor plugin. Full credential-entry UI is a future item (secrets are
          referenced by name, never entered in-app — Phase 7 posture).
        </p>
      </Card>

      {/* 7. Proxy — honest: not yet wired */}
      <Card
        title="Zero-instrumentation proxy"
        badge="⧗ not yet exposed"
        blurb="A base-URL swap that captures wire usage with no code. The proxy backend exists and is tested, but the live passthrough HTTP route isn't wired into this host yet — so there's no endpoint to point at today. Use OTLP or the SDK meanwhile."
      >
        <p className="muted small">Tracked as a remainder item; this card will carry a base-URL once the route lands.</p>
      </Card>
    </div>
  )
}

function Card({ title, badge, blurb, children }: {
  title: string; badge: string; blurb: string; children?: React.ReactNode;
}) {
  return (
    <div className="card">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 'var(--space-4)' }}>
        <h2 style={{ margin: 0 }}>{title}</h2>
        <span className="chip designed">{badge}</span>
      </div>
      <p className="muted" style={{ marginTop: 'var(--space-3)' }}>{blurb}</p>
      {children}
    </div>
  )
}
