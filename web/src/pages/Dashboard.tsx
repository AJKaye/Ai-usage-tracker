import { api, fmtNum, fmtUsd } from '../lib/api'
import { useApi } from '../lib/useApi'

export function Dashboard() {
  const { data, error, loading } = useApi(() => api.summary())

  if (loading) return <p className="muted">Loading…</p>
  if (error) return <p className="error">Could not load summary: {error}</p>
  if (!data) return null

  const providers = Object.entries(data.costByProvider).sort((a, b) => b[1] - a[1])
  const models = Object.entries(data.costByModel).sort((a, b) => b[1] - a[1])

  return (
    <div className="grid" style={{ gap: 'var(--space-8)' }}>
      <div className="grid cols-4">
        <div className="card stat"><div className="label">Total spend</div><div className="value">{fmtUsd(data.totalEstimatedCost)}</div><div className="sub">estimated</div></div>
        <div className="card stat"><div className="label">Events</div><div className="value">{fmtNum(data.spanCount)}</div><div className="sub">spans tracked</div></div>
        <div className="card stat"><div className="label">Input tokens</div><div className="value">{fmtNum(data.totalInputTokens)}</div></div>
        <div className="card stat"><div className="label">Output tokens</div><div className="value">{fmtNum(data.totalOutputTokens)}</div></div>
      </div>

      <div className="grid cols-2">
        <div className="card">
          <h2>Cost by provider</h2>
          <CostTable rows={providers} total={data.totalEstimatedCost} />
        </div>
        <div className="card">
          <h2>Cost by model</h2>
          <CostTable rows={models} total={data.totalEstimatedCost} />
        </div>
      </div>
    </div>
  )
}

function CostTable({ rows, total }: { rows: [string, number][]; total: number }) {
  if (rows.length === 0) return <p className="muted">No data yet — ingest some events.</p>
  return (
    <table>
      <thead><tr><th>Name</th><th className="num">Cost</th><th className="num">Share</th></tr></thead>
      <tbody>
        {rows.map(([name, cost]) => (
          <tr key={name}>
            <td>{name}</td>
            <td className="num">{fmtUsd(cost)}</td>
            <td className="num">{total > 0 ? `${((cost / total) * 100).toFixed(1)}%` : '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
