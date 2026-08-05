import { useEffect, useState } from 'react'
import { NavLink, Route, Routes } from 'react-router-dom'
import { Dashboard } from './pages/Dashboard'
import { Allocation } from './pages/Allocation'
import { Efficiency } from './pages/Efficiency'
import { Governance } from './pages/Governance'

// White-label + light/dark are pure design-system mechanisms: the theme is a
// [data-theme] scope, the color-scheme follows the OS unless toggled. Switching
// either restyles the whole app via re-pointed CSS custom properties — no edits here.
export function App() {
  const [theme, setTheme] = useState(() => localStorage.getItem('theme') ?? 'base')
  const [scheme, setScheme] = useState(() => localStorage.getItem('scheme') ?? 'light')
  const [tenant, setTenant] = useState(() => localStorage.getItem('tenant') ?? 'default')

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme)
    localStorage.setItem('theme', theme)
  }, [theme])
  useEffect(() => {
    document.documentElement.style.colorScheme = scheme
    document.documentElement.setAttribute('data-color-scheme', scheme)
    localStorage.setItem('scheme', scheme)
  }, [scheme])
  useEffect(() => { localStorage.setItem('tenant', tenant) }, [tenant])

  return (
    <div className="app">
      <nav className="sidebar" aria-label="Primary">
        <div className="brand">AI Usage Tracker</div>
        <NavLink to="/" end className="nav-link">Dashboard</NavLink>
        <NavLink to="/allocation" className="nav-link">Allocation</NavLink>
        <NavLink to="/efficiency" className="nav-link">Efficiency</NavLink>
        <NavLink to="/governance" className="nav-link">Regulatory Governance</NavLink>
      </nav>
      <div className="main">
        <div className="topbar">
          <h1>
            <Routes>
              <Route path="/" element={<>Cost &amp; Usage</>} />
              <Route path="/allocation" element={<>Cost Allocation</>} />
              <Route path="/efficiency" element={<>Efficiency</>} />
              <Route path="/governance" element={<>Regulatory Governance</>} />
            </Routes>
          </h1>
          <div className="controls">
            <label>
              <span className="muted" style={{ marginRight: 'var(--space-2)' }}>Tenant</span>
              <input className="btn" value={tenant} onChange={e => setTenant(e.target.value)} aria-label="Tenant" />
            </label>
            <select className="btn" value={theme} onChange={e => setTheme(e.target.value)} aria-label="Theme">
              <option value="base">Neutral</option>
              <option value="example-ssc">SS&amp;C (example)</option>
            </select>
            <button className="btn" onClick={() => setScheme(scheme === 'light' ? 'dark' : 'light')}
                    aria-label="Toggle light/dark">
              {scheme === 'light' ? '☾ Dark' : '☀ Light'}
            </button>
          </div>
        </div>

        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/allocation" element={<Allocation />} />
          <Route path="/efficiency" element={<Efficiency />} />
          <Route path="/governance" element={<Governance />} />
        </Routes>
      </div>
    </div>
  )
}
