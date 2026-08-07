import { useEffect, useMemo, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import {
  ReactFlow, Background, Controls, Handle, Position,
  type Node, type Edge, type NodeProps,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import '../styles/reactflow-theme.css'
import { api, fmtUsd, type WorkflowRun, type WorkflowDefinition } from '../lib/api'
import { useApi } from '../lib/useApi'

const TYPE_ACCENT: Record<string, string> = {
  transform: 'var(--chart-cat-1)', llm: 'var(--chart-cat-2)', http: 'var(--chart-cat-3)', agent: 'var(--chart-cat-4)',
}
const TERMINAL = new Set(['Succeeded', 'Failed', 'Canceled'])
type RunNodeData = { label: string; kind: string; status: string; cost?: number; preview?: string | null }

function RunNodeCard({ data }: NodeProps<Node<RunNodeData>>) {
  const accent = TYPE_ACCENT[data.kind] ?? 'var(--color-border-strong)'
  return (
    <div className={`wf-node status-${data.status}`} style={{ ['--wf-accent' as string]: accent }}>
      <Handle type="target" position={Position.Left} />
      <div className="wf-node-title">
        {data.label}
        <span className={`chip ${chipFor(data.status)}`} style={{ marginLeft: 'auto' }}>{data.status}</span>
      </div>
      <div className="wf-node-kind">{data.kind}</div>
      {data.cost != null && <div className="wf-node-sub">{fmtUsd(data.cost)}</div>}
      {data.preview && <div className="wf-node-sub" title={data.preview}>{data.preview}</div>}
      <Handle type="source" position={Position.Right} />
    </div>
  )
}
const nodeTypes = { wf: RunNodeCard }

function chipFor(status: string): string {
  if (status === 'Succeeded') return 'ok'
  if (status === 'Running') return 'running'
  if (status === 'Failed') return 'failed'
  if (status === 'Canceled' || status === 'Skipped') return 'designed'
  return 'designed'
}

export function WorkflowRun() {
  const { runId = '' } = useParams()
  // Client-side polling: bump `rev` every 3s until the run is terminal (no SSE — the tracker
  // has no streaming transport; polling reuses the useApi dep-bump idiom).
  const [rev, setRev] = useState(0)
  const run = useApi(() => api.getRun(runId), [runId, rev])
  const wf = useApi<WorkflowDefinition | null>(
    () => run.data ? api.getWorkflow(run.data.workflowId) : Promise.resolve(null), [run.data?.workflowId])

  const state = run.data?.state
  useEffect(() => {
    if (!state || TERMINAL.has(state)) return
    const t = setInterval(() => setRev(r => r + 1), 3000)
    return () => clearInterval(t)
  }, [state])

  // Per-node cost: for each node with a spanId, fetch its span's estimated cost.
  const [costs, setCosts] = useState<Record<string, number>>({})
  useEffect(() => {
    const nodes = run.data?.nodes ?? []
    nodes.filter(n => n.spanId && costs[n.nodeId] === undefined).forEach(n => {
      api.span(n.spanId!).then(s => {
        const cost = (s as { estimatedCost?: { totalCost?: number } }).estimatedCost?.totalCost
        if (cost != null) setCosts(c => ({ ...c, [n.nodeId]: cost }))
      }).catch(() => { /* span may not be persisted yet — retried on the next poll */ })
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [run.data])

  const { nodes, edges } = useMemo(() => buildGraph(run.data, wf.data ?? null, costs), [run.data, wf.data, costs])
  const totalCost = Object.values(costs).reduce((a, b) => a + b, 0)

  return (
    <div className="grid" style={{ gap: 'var(--space-6)' }}>
      <div className="grid cols-4">
        <div className="card stat"><div className="label">Run</div><div className="value" style={{ fontSize: 'var(--fs-lg)' }}>{runId.slice(0, 8)}</div>
          <div className="sub"><Link to="/workflows">← builder</Link></div></div>
        <div className="card stat"><div className="label">State</div>
          <div className="value"><span className={`chip ${chipFor(state ?? 'Pending')}`}>{state ?? '—'}</span></div>
          <div className="sub">{state && !TERMINAL.has(state) ? 'polling every 3s…' : 'final'}</div></div>
        <div className="card stat"><div className="label">Nodes done</div>
          <div className="value">{run.data ? run.data.nodes.filter(n => n.status === 'Succeeded').length : '—'}/{run.data?.nodes.length ?? '—'}</div></div>
        <div className="card stat"><div className="label">Run cost</div><div className="value">{fmtUsd(totalCost)}</div><div className="sub">from emitted spans</div></div>
      </div>

      {run.error && <p className="error">Could not load run: {run.error}</p>}
      {run.data?.error && <p className="error">Run error: {run.data.error}</p>}

      <div className="card" style={{ padding: 'var(--space-4)' }}>
        <div className="wf-canvas">
          <ReactFlow nodes={nodes} edges={edges} nodeTypes={nodeTypes} fitView
                     nodesDraggable={false} nodesConnectable={false}
                     proOptions={{ hideAttribution: true }}>
            <Background />
            <Controls showInteractive={false} />
          </ReactFlow>
        </div>
      </div>
    </div>
  )
}

function buildGraph(run: WorkflowRun | null, wf: WorkflowDefinition | null, costs: Record<string, number>):
  { nodes: Node<RunNodeData>[]; edges: Edge[] } {
  if (!run) return { nodes: [], edges: [] }
  const statusByNode = Object.fromEntries(run.nodes.map(n => [n.nodeId, n.status]))
  const previewByNode = Object.fromEntries(run.nodes.map(n => [n.nodeId, n.outputPreview]))
  const anyRunning = run.nodes.some(n => n.status === 'Running')

  // Prefer the saved layout/labels; fall back to a simple column if the definition is gone.
  const defNodes = wf?.nodes ?? run.nodes.map((n, i) => ({
    id: n.nodeId, name: n.nodeId, type: 'Transform', x: 60, y: 60 + i * 90,
    agentName: null, skillName: null, config: {}, inputSchema: [], outputSchema: [],
  } as WorkflowDefinition['nodes'][number]))

  const nodes: Node<RunNodeData>[] = defNodes.map(n => ({
    id: n.id, type: 'wf', position: { x: n.x, y: n.y }, draggable: false,
    data: {
      label: n.name ?? n.id, kind: n.type.toLowerCase(),
      status: statusByNode[n.id] ?? 'Pending', cost: costs[n.id], preview: previewByNode[n.id],
    },
  }))
  const edges: Edge[] = (wf?.edges ?? []).map((e, i) => {
    const active = anyRunning && statusByNode[e.fromNodeId] === 'Succeeded' && statusByNode[e.toNodeId] === 'Running'
    return {
      id: `e${i}`, source: e.fromNodeId, target: e.toNodeId,
      animated: active, className: active ? 'wf-edge-active' : undefined,
    }
  })
  return { nodes, edges }
}
