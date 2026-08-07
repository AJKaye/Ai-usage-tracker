import { useCallback, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  ReactFlow, Background, Controls, MiniMap, Handle, Position,
  addEdge, applyNodeChanges, applyEdgeChanges,
  type Node, type Edge, type Connection, type NodeChange, type EdgeChange, type NodeProps,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import '../styles/reactflow-theme.css'
import { api, fmtUsd, type WorkflowInput, type WorkflowNodeDto, type DryRunProjection } from '../lib/api'
import { useApi } from '../lib/useApi'

// Node-type → accent token (validated categorical palette; light+dark defined).
const TYPE_ACCENT: Record<string, string> = {
  transform: 'var(--chart-cat-1)', llm: 'var(--chart-cat-2)', http: 'var(--chart-cat-3)', agent: 'var(--chart-cat-4)',
}
const NODE_TYPES = ['transform', 'llm', 'http', 'agent'] as const
type NodeData = {
  label: string; kind: string; agent?: string; skill?: string;
  config: Record<string, string>; inputs: string[]; outputs: string[]; status?: string
}

const prefersReducedMotion = () =>
  typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

// A design-system-styled node (role tokens only, via the .wf-node classes).
function WorkflowNodeCard({ data, selected }: NodeProps<Node<NodeData>>) {
  const accent = TYPE_ACCENT[data.kind] ?? 'var(--color-border-strong)'
  return (
    <div className={`wf-node ${selected ? 'selected' : ''} ${data.status ? `status-${data.status}` : ''}`}
         style={{ ['--wf-accent' as string]: accent }}>
      <Handle type="target" position={Position.Left} />
      <div className="wf-node-title">{data.label || data.kind}</div>
      <div className="wf-node-kind">{data.kind}{data.skill ? ` · ${data.skill}` : ''}</div>
      {data.agent && <div className="wf-node-sub">agent: {data.agent}</div>}
      <Handle type="source" position={Position.Right} />
    </div>
  )
}
const nodeTypes = { wf: WorkflowNodeCard }

let seq = 0
const newId = (p: string) => `${p}-${Date.now().toString(36)}-${seq++}`

export function Workflows() {
  const navigate = useNavigate()
  const [rev, setRev] = useState(0)
  const list = useApi(() => api.workflows(), [rev])

  const [name, setName] = useState('New workflow')
  const [wfId, setWfId] = useState<string | null>(null)
  const [nodes, setNodes] = useState<Node<NodeData>[]>([])
  const [edges, setEdges] = useState<Edge[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [dry, setDry] = useState<DryRunProjection | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const onNodesChange = useCallback((c: NodeChange<Node<NodeData>>[]) => setNodes(n => applyNodeChanges(c, n)), [])
  const onEdgesChange = useCallback((c: EdgeChange[]) => setEdges(e => applyEdgeChanges(c, e)), [])
  const onConnect = useCallback((c: Connection) => setEdges(e => addEdge({ ...c, animated: !prefersReducedMotion() }, e)), [])

  const selected = useMemo(() => nodes.find(n => n.id === selectedId) ?? null, [nodes, selectedId])

  function addNode(kind: string) {
    const id = newId(kind)
    const node: Node<NodeData> = {
      id, type: 'wf',
      position: { x: 60 + nodes.length * 40, y: 80 + (nodes.length % 4) * 90 },
      data: {
        label: `${kind} node`, kind, config: {},
        inputs: kind === 'transform' || kind === 'http' ? ['input'] : ['prompt'],
        outputs: [kind === 'llm' ? 'completion' : kind === 'http' ? 'response' : kind === 'agent' ? 'result' : 'output'],
      },
    }
    setNodes(n => [...n, node])
    setSelectedId(id)
  }

  function patchSelected(patch: Partial<NodeData>) {
    if (!selectedId) return
    setNodes(ns => ns.map(n => n.id === selectedId ? { ...n, data: { ...n.data, ...patch } } : n))
  }

  function toInput(): WorkflowInput {
    const dtoNodes: WorkflowNodeDto[] = nodes.map(n => ({
      id: n.id, type: n.data.kind, name: n.data.label,
      agent_name: n.data.agent, skill_name: n.data.skill,
      config: n.data.config,
      inputs: n.data.inputs.map(name => ({ name })),
      outputs: n.data.outputs.map(name => ({ name })),
      x: n.position.x, y: n.position.y,
    }))
    const dtoEdges = edges.map(e => ({ from: e.source, to: e.target }))
    return { id: wfId ?? undefined, name, nodes: dtoNodes, edges: dtoEdges }
  }

  async function save() {
    setBusy(true); setErr(null)
    try {
      const saved = await api.saveWorkflow(toInput())
      setWfId(saved.id)
      setRev(r => r + 1)
    } catch (e) { setErr(String((e as Error).message ?? e)) } finally { setBusy(false) }
  }

  async function doDryRun() {
    setBusy(true); setErr(null); setDry(null)
    try {
      const saved = await api.saveWorkflow(toInput())
      setWfId(saved.id)
      setDry(await api.dryRun(saved.id, {}))
      setRev(r => r + 1)
    } catch (e) { setErr(String((e as Error).message ?? e)) } finally { setBusy(false) }
  }

  async function run() {
    setBusy(true); setErr(null)
    try {
      const saved = await api.saveWorkflow(toInput())
      const { runId } = await api.runWorkflow(saved.id, {})
      navigate(`/runs/${runId}`)
    } catch (e) { setErr(String((e as Error).message ?? e)); setBusy(false) }
  }

  function loadWorkflow(id: string) {
    api.getWorkflow(id).then(w => {
      setWfId(w.id); setName(w.name)
      setNodes(w.nodes.map(n => ({
        id: n.id, type: 'wf', position: { x: n.x, y: n.y },
        data: {
          label: n.name ?? n.type, kind: n.type.toLowerCase(),
          agent: n.agentName ?? undefined, skill: n.skillName ?? undefined,
          config: n.config, inputs: n.inputSchema.map(p => p.name), outputs: n.outputSchema.map(p => p.name),
        },
      })))
      setEdges(w.edges.map((e, i) => ({
        id: `e${i}`, source: e.fromNodeId, target: e.toNodeId, animated: !prefersReducedMotion(),
      })))
      setSelectedId(null); setDry(null)
    })
  }

  function newWorkflow() {
    setWfId(null); setName('New workflow'); setNodes([]); setEdges([]); setSelectedId(null); setDry(null)
  }

  return (
    <div className="grid" style={{ gap: 'var(--space-6)' }}>
      <div className="grid cols-2" style={{ gridTemplateColumns: '1fr 320px', gap: 'var(--space-6)' }}>
        {/* Canvas */}
        <div className="card" style={{ padding: 'var(--space-4)' }}>
          <div style={{ display: 'flex', gap: 'var(--space-2)', marginBottom: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'center' }}>
            <input className="btn" value={name} onChange={e => setName(e.target.value)} aria-label="Workflow name"
                   style={{ minWidth: 180 }} />
            <span className="muted small">Add:</span>
            {NODE_TYPES.map(t => <button key={t} className="btn btn-sm" onClick={() => addNode(t)}>+ {t}</button>)}
            <span style={{ flex: 1 }} />
            <button className="btn btn-sm" onClick={save} disabled={busy}>Save</button>
            <button className="btn btn-sm" onClick={doDryRun} disabled={busy}>Dry-run</button>
            <button className="btn" onClick={run} disabled={busy || nodes.length === 0}>▶ Run</button>
          </div>
          {err && <p className="error small">{err}</p>}
          <div className="wf-canvas">
            <ReactFlow
              nodes={nodes} edges={edges} nodeTypes={nodeTypes}
              onNodesChange={onNodesChange} onEdgesChange={onEdgesChange} onConnect={onConnect}
              onNodeClick={(_, n) => setSelectedId(n.id)} onPaneClick={() => setSelectedId(null)}
              fitView proOptions={{ hideAttribution: true }}>
              <Background />
              <Controls />
              <MiniMap pannable zoomable />
            </ReactFlow>
          </div>
          {dry && (
            <div style={{ marginTop: 'var(--space-3)' }}>
              <span className="chip designed">dry-run</span>{' '}
              order: <strong>{dry.order.join(' → ')}</strong>{' '}
              — projected <strong>{fmtUsd(dry.simulatedCost)}</strong>
            </div>
          )}
        </div>

        {/* Side panel: node config / IO, or the workflow list */}
        <div className="card wf-panel">
          {selected ? (
            <NodePanel key={selected.id} data={selected.data} onPatch={patchSelected}
                       onDelete={() => { setNodes(n => n.filter(x => x.id !== selected.id)); setSelectedId(null) }} />
          ) : (
            <>
              <h2 style={{ marginTop: 0 }}>Workflows</h2>
              <button className="btn btn-sm" onClick={newWorkflow}>+ New workflow</button>
              {list.loading && <p className="muted">Loading…</p>}
              {list.data && list.data.length === 0 && <p className="muted">No saved workflows yet.</p>}
              {list.data?.map(w => (
                <div key={w.id} className="wf-port-row" style={{ borderTop: '1px solid var(--color-border-subtle)', paddingTop: 'var(--space-2)' }}>
                  <button className="btn btn-sm" onClick={() => loadWorkflow(w.id)}>{w.name}</button>
                  <span className="muted small">{w.nodes.length} nodes</span>
                </div>
              ))}
              <p className="muted small">Select a node to edit its agent, skill, inputs/outputs, and config.</p>
            </>
          )}
        </div>
      </div>
    </div>
  )
}

function NodePanel({ data, onPatch, onDelete }: {
  data: NodeData; onPatch: (p: Partial<NodeData>) => void; onDelete: () => void
}) {
  const setConfig = (k: string, v: string) => onPatch({ config: { ...data.config, [k]: v } })
  return (
    <>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h2 style={{ margin: 0 }}>{data.kind} node</h2>
        <button className="btn btn-sm" onClick={onDelete}>Delete</button>
      </div>
      <label className="wf-field"><span>Label</span>
        <input className="btn" value={data.label} onChange={e => onPatch({ label: e.target.value })} /></label>
      <label className="wf-field"><span>Agent name (→ tracked as agent)</span>
        <input className="btn" value={data.agent ?? ''} onChange={e => onPatch({ agent: e.target.value })} /></label>
      <label className="wf-field"><span>Skill name (→ tracked as skill)</span>
        <input className="btn" value={data.skill ?? ''} onChange={e => onPatch({ skill: e.target.value })} /></label>

      {(data.kind === 'llm' || data.kind === 'agent') && (
        <>
          <label className="wf-field"><span>Model</span>
            <input className="btn" value={data.config.model ?? ''} placeholder="claude-opus-5"
                   onChange={e => setConfig('model', e.target.value)} /></label>
          <label className="wf-field"><span>Prompt template (use {'{input}'})</span>
            <textarea className="btn" rows={3} value={data.config.prompt ?? ''}
                      onChange={e => setConfig('prompt', e.target.value)} /></label>
        </>
      )}
      {data.kind === 'http' && (
        <label className="wf-field"><span>URL</span>
          <input className="btn" value={data.config.url ?? ''} onChange={e => setConfig('url', e.target.value)} /></label>
      )}
      {data.kind === 'transform' && (
        <>
          <label className="wf-field"><span>Op</span>
            <select className="btn" value={data.config.op ?? 'passthrough'} onChange={e => setConfig('op', e.target.value)}>
              <option value="passthrough">passthrough</option>
              <option value="template">template</option>
              <option value="concat">concat</option>
            </select></label>
          {data.config.op === 'template' && (
            <label className="wf-field"><span>Template</span>
              <textarea className="btn" rows={2} value={data.config.template ?? ''}
                        onChange={e => setConfig('template', e.target.value)} /></label>
          )}
        </>
      )}

      <div className="wf-field">
        <span>Inputs</span>
        <input className="btn" value={data.inputs.join(', ')}
               onChange={e => onPatch({ inputs: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })} />
      </div>
      <div className="wf-field">
        <span>Outputs</span>
        <input className="btn" value={data.outputs.join(', ')}
               onChange={e => onPatch({ outputs: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })} />
      </div>
    </>
  )
}
