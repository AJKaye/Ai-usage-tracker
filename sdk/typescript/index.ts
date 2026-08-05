// AI Usage Tracker — thin TypeScript SDK (zero dependencies; uses global fetch).
// Convenience over the raw /v1 endpoints so a new surface integrates in a few lines.
// The raw HTTP endpoints are always sufficient; this just types them.

export interface UsageTrackerOptions {
  /** Base URL of the tracker, e.g. "http://localhost:5000" or "https://usage.example.com". */
  baseUrl: string;
  /** Tenant id (dev/self-host). In SaaS the apiKey resolves the tenant server-side. */
  tenant?: string;
  /** Bearer API key (SaaS / authenticated deployments). */
  apiKey?: string;
}

export interface GenAiEvent {
  provider: string;
  model?: string;
  inputTokens?: number;
  outputTokens?: number;
  cacheReadInputTokens?: number;
  cacheCreationInputTokens?: number;
  reasoningOutputTokens?: number;
  spanId?: string;
  traceId?: string;
  kind?: 'llm' | 'agent' | 'tool' | 'chain' | 'retriever' | 'embedding';
}

export interface UsageSummary {
  spanCount: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalEstimatedCost: number;
  currency: string;
  costByProvider: Record<string, number>;
  costByModel: Record<string, number>;
}

export class UsageTracker {
  constructor(private readonly opts: UsageTrackerOptions) {}

  private headers(): Record<string, string> {
    const h: Record<string, string> = { 'Content-Type': 'application/json' };
    if (this.opts.tenant) h['X-Tenant-Id'] = this.opts.tenant;
    if (this.opts.apiKey) h['Authorization'] = `Bearer ${this.opts.apiKey}`;
    return h;
  }

  private async post(path: string, body: unknown): Promise<Response> {
    const res = await fetch(this.opts.baseUrl + path, {
      method: 'POST', headers: this.headers(), body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`${path} → HTTP ${res.status}`);
    return res;
  }

  /** Ingest one gen_ai.* usage event (the flat-JSON ingest path). */
  async ingest(e: GenAiEvent): Promise<void> {
    await this.post('/v1/ingest', {
      'gen_ai.provider.name': e.provider,
      'gen_ai.response.model': e.model,
      'gen_ai.usage.input_tokens': e.inputTokens,
      'gen_ai.usage.output_tokens': e.outputTokens,
      'gen_ai.usage.cache_read.input_tokens': e.cacheReadInputTokens,
      'gen_ai.usage.cache_creation.input_tokens': e.cacheCreationInputTokens,
      'gen_ai.usage.reasoning.output_tokens': e.reasoningOutputTokens,
      span_id: e.spanId,
      trace_id: e.traceId,
      kind: e.kind ?? 'llm',
    });
  }

  /** Send a coarse usage event (RPA units, seats, premium requests) as a CloudEvent. */
  async sendUsageEvent(type: string, source: string, data: Record<string, unknown>): Promise<void> {
    await this.post('/v1/events', { specversion: '1.0', type, source, id: crypto.randomUUID(), data });
  }

  /** Attach an externally-computed eval score to a span/trace (be the aggregator). */
  async postScore(targetId: string, name: string, value: number | string | boolean, source?: string): Promise<void> {
    const body: Record<string, unknown> = { target_id: targetId, name, source };
    if (typeof value === 'number') body.numeric = value;
    else if (typeof value === 'boolean') body.boolean = value;
    else body.category = value;
    await this.post('/v1/scores', body);
  }

  /** Query the rolled-up usage/cost summary for the tenant. */
  async summary(): Promise<UsageSummary> {
    const res = await fetch(this.opts.baseUrl + '/v1/summary', { headers: this.headers() });
    if (!res.ok) throw new Error(`/v1/summary → HTTP ${res.status}`);
    return res.json() as Promise<UsageSummary>;
  }
}
