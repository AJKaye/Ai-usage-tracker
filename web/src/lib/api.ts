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

async function post<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(path, {
    method: 'POST',
    headers: { ...headers(), 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`${path} → HTTP ${res.status}`);
  return res.json() as Promise<T>;
}

async function del<T>(path: string): Promise<T> {
  const res = await fetch(path, { method: 'DELETE', headers: headers() });
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

// --- FinOps control plane (Phase 11): budgets, status, anomalies, forecast, alerts ---
export interface Budget {
  id: string; tenantId: string; dimension: string; dimensionValue: string | null;
  limit: number; currency: string; period: string; warnAtFraction: number;
}
export interface BudgetInput {
  dimension?: string; dimension_value?: string | null;
  limit: number; currency?: string; period?: string; warn_at_fraction?: number;
}
export interface BudgetStatus {
  budget: Budget; spentToDate: number; limit: number; utilization: number;
  projectedEndOfPeriod: number; state: 'ok' | 'warning' | 'exceeded';
}
export interface DailyCost { day: string; cost: number; spanCount: number; currency: string; }
export interface AnomalyResult {
  day: string; cost: number; baselineMean: number; expectedUpperBound: number;
  zScore: number | null; currency: string;
}
export interface AnomalyResponse { anomaly: AnomalyResult | null; series: DailyCost[]; }
export interface ForecastResponse {
  month: string; spentToDate: number; projectedEndOfMonth: number; series: DailyCost[];
}
export interface Alert {
  id: string; tenantId: string; kind: string; message: string;
  value: number | null; at: string; reference: string | null;
}

export const api = {
  summary: () => get<UsageSummary>('/v1/summary'),
  allocation: (dimension: string) => get<AllocationResponse>(`/v1/allocation?dimension=${encodeURIComponent(dimension)}`),
  unitEconomics: (outcomes?: number) => get<UnitEconomics>(`/v1/unit-economics${outcomes ? `?outcomes=${outcomes}` : ''}`),
  efficiency: () => get<Efficiency>('/v1/efficiency'),
  governance: () => get<GovernanceMatrix>('/v1/governance'),
  // FinOps control plane
  budgets: () => get<Budget[]>('/v1/budgets'),
  budgetStatus: () => get<BudgetStatus[]>('/v1/budgets/status'),
  createBudget: (b: BudgetInput) => post<Budget>('/v1/budgets', b),
  deleteBudget: (id: string) => del<{ deleted: string }>(`/v1/budgets/${encodeURIComponent(id)}`),
  anomalies: () => get<AnomalyResponse>('/v1/anomalies'),
  forecast: () => get<ForecastResponse>('/v1/forecast'),
  alerts: (limit = 50) => get<Alert[]>(`/v1/alerts?limit=${limit}`),
};

export const fmtUsd = (n: number) =>
  new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 4 }).format(n);
export const fmtNum = (n: number) => new Intl.NumberFormat().format(n);
export const fmtPct = (n: number) => `${(n * 100).toFixed(1)}%`;
