import { api, type GovernanceControl } from '../lib/api'
import { useApi } from '../lib/useApi'

// The Regulatory Governance page (D6). Sourced live from GET /v1/governance, which
// parses GOVERNANCE.md — so this page never drifts from the maintained control
// register. A control-status change in GOVERNANCE.md shows here with no code edit.
export function Governance() {
  const { data, error, loading } = useApi(() => api.governance())

  if (loading) return <p className="muted">Loading…</p>
  if (error) return <p className="error">Could not load governance matrix: {error}</p>
  if (!data) return null

  return (
    <div className="grid" style={{ gap: 'var(--space-8)' }}>
      <p className="muted">
        How this deployment meets SOC 2 · GDPR · HIPAA · FedRAMP controls. Sourced from
        the maintained control register (last updated {data.lastUpdated}). This is a
        design-time register, not a certification.
      </p>

      <div className="grid cols-4">
        {Object.entries(data.statusCounts).map(([status, count]) => (
          <div className="card stat" key={status}>
            <div className="label">{status}</div>
            <div className="value">{count}</div>
          </div>
        ))}
      </div>

      <div className="card">
        <h2>Control register</h2>
        <table>
          <thead>
            <tr><th>ID</th><th>Control</th><th>Mechanism</th><th>Status</th><th>Evidence</th></tr>
          </thead>
          <tbody>
            {data.controls.map(c => (
              <tr key={c.id}>
                <td><strong>{c.id}</strong></td>
                <td>{c.control}</td>
                <td className="muted">{c.mechanism}</td>
                <td><StatusChip status={c.status} /></td>
                <td className="muted">{c.evidence}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function StatusChip({ status }: { status: GovernanceControl['status'] }) {
  const key = status.toLowerCase().split(' ')[0]   // "verified (app-layer)" → "verified"
  return <span className={`chip ${key}`}>{status}</span>
}
