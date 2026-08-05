import { api, fmtNum, fmtPct, fmtUsd } from '../lib/api'
import { useApi } from '../lib/useApi'

export function Efficiency() {
  const eff = useApi(() => api.efficiency())
  const ue = useApi(() => api.unitEconomics(undefined))

  return (
    <div className="grid" style={{ gap: 'var(--space-8)' }}>
      <div className="grid cols-4">
        <Tile label="Avg duration" value={eff.data ? `${eff.data.avgDurationMs.toFixed(0)} ms` : '—'} loading={eff.loading} />
        <Tile label="Avg TTFT" value={eff.data?.avgTimeToFirstTokenMs != null ? `${eff.data.avgTimeToFirstTokenMs.toFixed(0)} ms` : '—'} loading={eff.loading} />
        <Tile label="Cache hit rate" value={eff.data ? fmtPct(eff.data.cacheHitRate) : '—'} loading={eff.loading} />
        <Tile label="Error rate" value={eff.data ? fmtPct(eff.data.errorRate) : '—'} loading={eff.loading} />
      </div>

      <div className="card">
        <h2>Unit economics</h2>
        {ue.error && <p className="error">Could not load: {ue.error}</p>}
        {ue.data && (
          <table>
            <tbody>
              <tr><td>Cost per token</td><td className="num">{fmtUsd(ue.data.costPerToken)}</td></tr>
              <tr><td>Cost per inference</td><td className="num">{fmtUsd(ue.data.costPerInference)}</td></tr>
              <tr><td>Total tokens</td><td className="num">{eff.data ? fmtNum(eff.data.totalTokens) : '—'}</td></tr>
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}

function Tile({ label, value, loading }: { label: string; value: string; loading: boolean }) {
  return (
    <div className="card stat">
      <div className="label">{label}</div>
      <div className="value">{loading ? '…' : value}</div>
    </div>
  )
}
