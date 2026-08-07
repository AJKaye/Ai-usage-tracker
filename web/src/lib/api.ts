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

// --- Phase 12: visual workflow builder + execution ---
export type WorkflowNodeType = 'Llm' | 'Http' | 'Agent' | 'Transform';
export type RunState = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Canceled';
export type NodeStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped' | 'Canceled';

export interface NodePortDto { name: string; type?: string; required?: boolean; description?: string; }
export interface WorkflowNodeDto {
  id: string; type: string;                         // llm|http|agent|transform (lowercase on the wire)
  agent_name?: string; skill_name?: string; name?: string;
  config?: Record<string, string>;
  inputs?: NodePortDto[]; outputs?: NodePortDto[];
  x: number; y: number;
}
export interface EdgeMappingDto { from_output: string; to_input: string; }
export interface WorkflowEdgeDto { from: string; to: string; mapping?: EdgeMappingDto[]; }
export interface WorkflowInput { id?: string; name: string; nodes: WorkflowNodeDto[]; edges: WorkflowEdgeDto[]; }

// Server shape (PascalCase enums, camelCase fields from the contract records).
export interface WorkflowNode {
  id: string; type: WorkflowNodeType; agentName: string | null; skillName: string | null;
  name: string | null; config: Record<string, string>;
  inputSchema: { name: string; type: string; required: boolean; description: string | null }[];
  outputSchema: { name: string; type: string; required: boolean; description: string | null }[];
  x: number; y: number;
}
export interface WorkflowEdge { fromNodeId: string; toNodeId: string; mapping: { fromOutput: string; toInput: string }[]; }
export interface WorkflowDefinition {
  id: string; tenantId: string; name: string; version: number; updatedAt: string;
  nodes: WorkflowNode[]; edges: WorkflowEdge[];
}
export interface NodeRunState {
  nodeId: string; status: NodeStatus; startedAt: string | null; endedAt: string | null;
  spanId: string | null; error: string | null; outputPreview: string | null;
}
export interface WorkflowRun {
  runId: string; workflowId: string; tenantId: string; state: RunState;
  startedAt: string; endedAt: string | null; workflowVersion: number;
  nodes: NodeRunState[]; error: string | null;
}
export interface DryRunNode { nodeId: string; kind: string; simulatedCost: number; inputTokens: number; outputTokens: number; }
export interface DryRunProjection { order: string[]; simulatedCost: number; currency: string; perNode: DryRunNode[]; }

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
  // Workflow builder + execution
  workflows: () => get<WorkflowDefinition[]>('/v1/workflows'),
  getWorkflow: (id: string) => get<WorkflowDefinition>(`/v1/workflows/${encodeURIComponent(id)}`),
  saveWorkflow: (w: WorkflowInput) => post<WorkflowDefinition>('/v1/workflows', w),
  deleteWorkflow: (id: string) => del<{ deleted: string }>(`/v1/workflows/${encodeURIComponent(id)}`),
  dryRun: (id: string, inputs: Record<string, string>) =>
    post<DryRunProjection>(`/v1/workflows/${encodeURIComponent(id)}/dry-run`, { inputs }),
  runWorkflow: (id: string, inputs: Record<string, string>) =>
    post<{ runId: string }>(`/v1/workflows/${encodeURIComponent(id)}/run`, { inputs }),
  getRun: (runId: string) => get<WorkflowRun>(`/v1/runs/${encodeURIComponent(runId)}`),
  listRuns: (workflowId?: string) =>
    get<WorkflowRun[]>(`/v1/runs${workflowId ? `?workflowId=${encodeURIComponent(workflowId)}` : ''}`),
  cancelRun: (runId: string) =>
    post<{ runId: string; cancelRequested: boolean; state: string }>(`/v1/runs/${encodeURIComponent(runId)}/cancel`, {}),
  span: (spanId: string) => get<Record<string, unknown>>(`/v1/spans/${encodeURIComponent(spanId)}`),
};

export const fmtUsd = (n: number) =>
  new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 4 }).format(n);
export const fmtNum = (n: number) => new Intl.NumberFormat().format(n);
export const fmtPct = (n: number) => `${(n * 100).toFixed(1)}%`;
