import { useState } from 'react'
import { api, fmtUsd, fmtPct, type BudgetInput } from '../lib/api'
import { useApi } from '../lib/useApi'

// FinOps control plane (Phase 11): set spend limits, watch utilization + a month-end
// forecast, and see budget/anomaly alerts. Role tokens only — no raw color.
const DIMENSIONS = ['', 'provider', 'model', 'team', 'user', 'environment']

export function Budgets() {
  // Bump this to refetch all panels after a create/delete.
  const [rev, setRev] = useState(0)
  const refresh = () => setRev(r => r + 1)

  const status = useApi(() => api.budgetStatus(), [rev])
  const forecast = useApi(() => api.forecast(), [rev])
  const anomalies = useApi(() => api.anomalies(), [rev])
  const alerts = useApi(() => api.alerts(), [rev])

  return (
    <div className="grid" style={{ gap: 'var(--space-8)' }}>
      {/* Forecast tiles */}
      <div className="grid cols-4">
        <div className="card stat">
          <div className="label">Month to date</div>
          <div className="value">{forecast.data ? fmtUsd(forecast.data.spentToDate) : '—'}</div>
          <div className="sub">{forecast.data?.month ?? 'current month'}</div>
        </div>
        <div className="card stat">
          <div className="label">Projected month end</div>
          <div className="value">{forecast.data ? fmtUsd(forecast.data.projectedEndOfMonth) : '—'}</div>
          <div className="sub">run-rate projection</div>
        </div>
        <div className="card stat">
          <div className="label">Active budgets</div>
          <div className="value">{status.data?.length ?? '—'}</div>
        </div>
        <div className="card stat">
          <div className="label">Open alerts</div>
          <div className="value">{alerts.data?.length ?? '—'}</div>
          <div className="sub">in-app feed</div>
        </div>
      </div>

      <div className="grid cols-2">
        {/* Budgets + utilization */}
        <div className="card">
          <h2>Budgets</h2>
          {status.loading && <p className="muted">Loading…</p>}
          {status.error && <p className="error">Could not load budgets: {status.error}</p>}
          {status.data && status.data.length === 0 && <p className="muted">No budgets yet — create one below.</p>}
          {status.data && status.data.map(s => {
            const pct = Math.min(s.utilization * 100, 100)
            return (
              <div key={s.budget.id} style={{ marginBottom: 'var(--space-5)' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 'var(--space-2)' }}>
                  <span>
                    {scopeLabel(s.budget.dimension, s.budget.dimensionValue)}{' '}
                    <span className="muted small">/ {s.budget.period}</span>
                  </span>
                  <span className={`chip ${s.state}`}>{s.state}</span>
                </div>
                <div className="bar-row">
                  <span className="num small muted">{fmtPct(s.utilization)}</span>
                  <span className="bar-track"><span className="bar-fill" style={{ width: `${pct}%` }} /></span>
                  <span className="num">{fmtUsd(s.spentToDate)} / {fmtUsd(s.limit)}</span>
                </div>
                <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 'var(--space-1)' }}>
                  <span className="muted small">projected {fmtUsd(s.projectedEndOfPeriod)}</span>
                  <button className="btn btn-sm" onClick={() => api.deleteBudget(s.budget.id).then(refresh)}>Delete</button>
                </div>
              </div>
            )
          })}
        </div>

        {/* Create-budget form */}
        <div className="card">
          <h2>New budget</h2>
          <CreateBudgetForm onCreated={refresh} />
        </div>
      </div>

      <div className="grid cols-2">
        {/* Anomaly */}
        <div className="card">
          <h2>Cost anomaly</h2>
          {anomalies.loading && <p className="muted">Loading…</p>}
          {anomalies.error && <p className="error">Could not load anomalies: {anomalies.error}</p>}
          {anomalies.data && !anomalies.data.anomaly && (
            <p className="muted">No anomaly detected in the trailing window — spend is within its baseline.</p>
          )}
          {anomalies.data?.anomaly && (
            <p>
              <span className="chip exceeded">spike</span>{' '}
              <strong>{fmtUsd(anomalies.data.anomaly.cost)}</strong> on {anomalies.data.anomaly.day} vs baseline{' '}
              {fmtUsd(anomalies.data.anomaly.baselineMean)}
              {anomalies.data.anomaly.zScore != null && <span className="muted small"> (z = {anomalies.data.anomaly.zScore.toFixed(1)})</span>}.
            </p>
          )}
        </div>

        {/* Alert feed */}
        <div className="card">
          <h2>Alerts</h2>
          {alerts.loading && <p className="muted">Loading…</p>}
          {alerts.error && <p className="error">Could not load alerts: {alerts.error}</p>}
          {alerts.data && alerts.data.length === 0 && <p className="muted">No alerts — you're within budget.</p>}
          {alerts.data && alerts.data.length > 0 && (
            <table>
              <thead><tr><th>Kind</th><th>Message</th><th className="num">When</th></tr></thead>
              <tbody>
                {alerts.data.map(a => (
                  <tr key={a.id}>
                    <td><span className={`chip ${chipFor(a.kind)}`}>{a.kind.replace('_', ' ')}</span></td>
                    <td className="small">{a.message}</td>
                    <td className="num small muted">{new Date(a.at).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  )
}

function scopeLabel(dimension: string, value: string | null): string {
  if (!dimension) return 'Whole tenant'
  return value ? `${dimension} = ${value}` : `${dimension} (aggregate)`
}

// Map an alert kind to an existing chip variant.
function chipFor(kind: string): string {
  if (kind === 'budget_exceeded') return 'exceeded'
  if (kind === 'budget_warning') return 'warning'
  return 'designed'   // cost_anomaly / other → neutral
}

function CreateBudgetForm({ onCreated }: { onCreated: () => void }) {
  const [dimension, setDimension] = useState('')
  const [dimensionValue, setDimensionValue] = useState('')
  const [limit, setLimit] = useState('100')
  const [period, setPeriod] = useState('monthly')
  const [warn, setWarn] = useState('0.8')
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const n = Number(limit)
    if (!(n > 0)) { setErr('Limit must be greater than zero.'); return }
    const body: BudgetInput = {
      dimension: dimension || undefined,
      dimension_value: dimension && dimensionValue ? dimensionValue : undefined,
      limit: n,
      period,
      warn_at_fraction: Number(warn) || undefined,
    }
    setBusy(true); setErr(null)
    try {
      await api.createBudget(body)
      setDimensionValue('')
      onCreated()
    } catch (e) {
      setErr(String((e as Error).message ?? e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} className="grid" style={{ gap: 'var(--space-4)' }}>
      <label className="grid" style={{ gap: 'var(--space-2)' }}>
        <span className="small muted">Scope</span>
        <select className="btn" value={dimension} onChange={e => setDimension(e.target.value)} aria-label="Budget dimension">
          {DIMENSIONS.map(d => <option key={d || 'tenant'} value={d}>{d === '' ? 'Whole tenant' : d}</option>)}
        </select>
      </label>
      {dimension && (
        <label className="grid" style={{ gap: 'var(--space-2)' }}>
          <span className="small muted">{dimension} value <span className="muted">(blank = aggregate)</span></span>
          <input className="btn" value={dimensionValue} onChange={e => setDimensionValue(e.target.value)}
                 placeholder={`e.g. a specific ${dimension}`} aria-label="Dimension value" />
        </label>
      )}
      <label className="grid" style={{ gap: 'var(--space-2)' }}>
        <span className="small muted">Limit (USD)</span>
        <input className="btn" type="number" min="0" step="0.01" value={limit}
               onChange={e => setLimit(e.target.value)} aria-label="Budget limit" />
      </label>
      <label className="grid" style={{ gap: 'var(--space-2)' }}>
        <span className="small muted">Period</span>
        <select className="btn" value={period} onChange={e => setPeriod(e.target.value)} aria-label="Budget period">
          <option value="monthly">Monthly</option>
          <option value="daily">Daily</option>
        </select>
      </label>
      <label className="grid" style={{ gap: 'var(--space-2)' }}>
        <span className="small muted">Warn at (fraction, 0–1)</span>
        <input className="btn" type="number" min="0" max="1" step="0.05" value={warn}
               onChange={e => setWarn(e.target.value)} aria-label="Warn-at fraction" />
      </label>
      {err && <p className="error small">{err}</p>}
      <button className="btn" type="submit" disabled={busy}>{busy ? 'Saving…' : 'Create budget'}</button>
    </form>
  )
}
