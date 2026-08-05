import { useState } from 'react'
import { api, fmtUsd } from '../lib/api'
import { useApi } from '../lib/useApi'

const DIMENSIONS = ['provider', 'model', 'team', 'user', 'environment']

export function Allocation() {
  const [dimension, setDimension] = useState('provider')
  const { data, error, loading } = useApi(() => api.allocation(dimension), [dimension])

  return (
    <div className="card">
      <h2>
        Tag-free allocation by{' '}
        <select className="btn" value={dimension} onChange={e => setDimension(e.target.value)} aria-label="Allocation dimension">
          {DIMENSIONS.map(d => <option key={d} value={d}>{d}</option>)}
        </select>
      </h2>

      {loading && <p className="muted">Loading…</p>}
      {error && <p className="error">Could not load allocation: {error}</p>}
      {data && data.buckets.length === 0 && <p className="muted">No spend to allocate yet.</p>}
      {data && data.buckets.length > 0 && (
        <>
          {data.buckets.map(b => {
            const pct = data.total > 0 ? (b.cost / data.total) * 100 : 0
            return (
              <div className="bar-row" key={b.key}>
                <span title={b.key} style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{b.key}</span>
                <span className="bar-track"><span className="bar-fill" style={{ width: `${pct}%` }} /></span>
                <span className="num">{fmtUsd(b.cost)}</span>
              </div>
            )
          })}
          <p className="muted" style={{ marginTop: 'var(--space-5)' }}>
            Total allocated: {fmtUsd(data.total)} — 100% of spend, no upstream tags required.
          </p>
        </>
      )}
    </div>
  )
}
