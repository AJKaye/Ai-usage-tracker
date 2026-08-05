// Thin typed client for the /v1 serving API. Sends the tenant header (dev) or a
// bearer token if configured; the API scopes everything to the resolved principal.

const tenant = () => localStorage.getItem('tenant') ?? 'default';
const token = () => localStorage.getItem('apiKey');

function headers(): HeadersInit {
  const h: Record<string, string> = { 'X-Tenant-Id': tenant() };
  const t = token();
  if (t) h['Authorization'] = `Bearer ${t}`;
  return h;
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(path, { headers: headers() });
  if (!res.ok) throw new Error(`${path} → HTTP ${res.status}`);
  return res.json() as Promise<T>;
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

export interface AllocationBucket { key: string; cost: number; currency: string; spanCount: number; }
export interface AllocationResponse { dimension: string; buckets: AllocationBucket[]; total: number; }

export interface UnitEconomics {
  costPerToken: number;
  costPerInference: number;
  costPerOutcome: number | null;
  outcomes: number | null;
}

export interface Efficiency {
  spanCount: number;
  avgDurationMs: number;
  avgTimeToFirstTokenMs: number | null;
  cacheHitRate: number;
  errorRate: number;
  totalTokens: number;
}

export interface GovernanceControl {
  id: string; control: string; mechanism: string; status: string; evidence: string;
}
export interface GovernanceMatrix {
  controls: GovernanceControl[];
  lastUpdated: string;
  statusCounts: Record<string, number>;
}

export const api = {
  summary: () => get<UsageSummary>('/v1/summary'),
  allocation: (dimension: string) => get<AllocationResponse>(`/v1/allocation?dimension=${encodeURIComponent(dimension)}`),
  unitEconomics: (outcomes?: number) => get<UnitEconomics>(`/v1/unit-economics${outcomes ? `?outcomes=${outcomes}` : ''}`),
  efficiency: () => get<Efficiency>('/v1/efficiency'),
  governance: () => get<GovernanceMatrix>('/v1/governance'),
};

export const fmtUsd = (n: number) =>
  new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 4 }).format(n);
export const fmtNum = (n: number) => new Intl.NumberFormat().format(n);
export const fmtPct = (n: number) => `${(n * 100).toFixed(1)}%`;
