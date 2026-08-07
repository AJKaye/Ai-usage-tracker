using System.Text.Json.Serialization;

namespace UsageTracker.Contracts;

// ============================================================================
//  Visual Agent Workflow Builder + Live Execution (Phase 12).
//  A tenant designs a graph of "agents" that perform "skills" and hand off to
//  each other; the tracker EXECUTES it (LLM / HTTP / agent-loop / transform
//  nodes) and each executed step is emitted as a canonical Span through the
//  SAME IIngestChannel — so cost/allocation/budgets/efficiency apply for free.
//  One run == one TraceId; a handoff == a parent→child span; nodes bind to
//  telemetry by Metadata["agent"] + Metadata["skill"]. Execution is OPT-IN
//  (the 'execution' profile) and egress-gated; the default build stays
//  air-gapped. All records are serialization-trivial (strings + small lists),
//  mirroring SpanQuery/Budget.
// ============================================================================

/// <summary>What a node does when the workflow runs. Dispatch key for <see cref="INodeExecutor"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowNodeType
{
    /// <summary>Calls a model with a templated prompt built from upstream outputs.</summary>
    Llm,
    /// <summary>Calls an external HTTP API / tool with mapped inputs.</summary>
    Http,
    /// <summary>Runs a bounded LLM+tool sub-loop until done.</summary>
    Agent,
    /// <summary>Pure, deterministic transform over inputs — no network, no cost.</summary>
    Transform,
}

/// <summary>Lifecycle state of a whole workflow run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunState { Pending, Running, Succeeded, Failed, Canceled }

/// <summary>Lifecycle state of a single node within a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NodeStatus { Pending, Running, Succeeded, Failed, Skipped, Canceled }

/// <summary>Coarse type of a node input/output port (kept small on purpose).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortType { Text, Json, Number, Boolean }

/// <summary>
/// One named input or output port on a node — the "custom inputs/outputs" a node
/// declares. Values flow at runtime as strings (see <see cref="NodeExecutionResult.Outputs"/>),
/// the same discipline as <see cref="Span.Metadata"/>, so everything round-trips as JSON
/// and stays air-gap-trivial.
/// </summary>
public sealed record NodePort
{
    public required string Name { get; init; }
    public PortType Type { get; init; } = PortType.Text;
    public bool Required { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// One node on the canvas — an agent performing a skill. <see cref="AgentName"/> and
/// <see cref="SkillName"/> are stamped onto the emitted span's Metadata (keys "agent"/"skill"),
/// which is how observed telemetry (cost, latency, tokens) binds back to the node. The
/// executor-specific <see cref="Config"/> holds e.g. llm→{model,prompt,maxTokens,secretName},
/// http→{method,url,secretName,bodyTemplate}, transform→{op,template}, agent→{model,systemPrompt,maxIterations}.
/// </summary>
public sealed record WorkflowNode
{
    public required string Id { get; init; }
    public required WorkflowNodeType Type { get; init; }
    public string? AgentName { get; init; }              // → Metadata["agent"]
    public string? SkillName { get; init; }              // → Metadata["skill"]
    public string? Name { get; init; }                   // display label
    public IReadOnlyDictionary<string, string> Config { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<NodePort> InputSchema { get; init; } = Array.Empty<NodePort>();
    public IReadOnlyList<NodePort> OutputSchema { get; init; } = Array.Empty<NodePort>();
    public double X { get; init; }                       // canvas position
    public double Y { get; init; }
}

/// <summary>Maps one upstream output port to one downstream input port along an edge.</summary>
public sealed record EdgeMapping(string FromOutput, string ToInput);

/// <summary>
/// A directed handoff from one node to another. <see cref="Mapping"/> wires specific
/// upstream outputs to downstream inputs; an empty mapping means "pass the whole upstream
/// output bag by name-match".
/// </summary>
public sealed record WorkflowEdge
{
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public IReadOnlyList<EdgeMapping> Mapping { get; init; } = Array.Empty<EdgeMapping>();
}

/// <summary>A saved, tenant-owned workflow graph. Must be a DAG (cycles rejected at save).</summary>
public sealed record WorkflowDefinition
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<WorkflowNode> Nodes { get; init; } = Array.Empty<WorkflowNode>();
    public IReadOnlyList<WorkflowEdge> Edges { get; init; } = Array.Empty<WorkflowEdge>();
    public int Version { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Live state of one node within a run — the unit the polling overlay renders. <see cref="SpanId"/>
/// joins the node to its emitted span so the overlay can pull cost/latency/tokens from
/// <c>GET /v1/spans/{spanId}</c>.
/// </summary>
public sealed record NodeRunState
{
    public required string NodeId { get; init; }
    public NodeStatus Status { get; init; } = NodeStatus.Pending;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? SpanId { get; init; }
    public string? Error { get; init; }
    /// <summary>Truncated, NON-PII summary of the node's output (never the raw prompt/response text).</summary>
    public string? OutputPreview { get; init; }
}

/// <summary>
/// A single execution of a workflow. <see cref="RunId"/> IS the TraceId of every span the
/// run emits — so the run's telemetry is exactly <c>SpanQuery{TraceId=RunId}</c>.
/// </summary>
public sealed record WorkflowRun
{
    public required string RunId { get; init; }          // == TraceId
    public required string WorkflowId { get; init; }
    public required string TenantId { get; init; }
    public RunState State { get; init; } = RunState.Pending;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int WorkflowVersion { get; init; }
    public IReadOnlyList<NodeRunState> Nodes { get; init; } = Array.Empty<NodeRunState>();
    public string? Error { get; init; }
}

/// <summary>
/// Stores tenant workflow definitions (mirrors <see cref="IBudgetStore"/>). In-memory now;
/// a durable store satisfies the same contract in the scale tier.
/// </summary>
public interface IWorkflowStore
{
    Task UpsertAsync(WorkflowDefinition workflow, CancellationToken ct = default);
    Task<WorkflowDefinition?> GetAsync(string tenantId, string workflowId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(string tenantId, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string workflowId, CancellationToken ct = default);
}

/// <summary>Stores workflow runs + their live per-node state (the overlay's poll target).</summary>
public interface IRunStore
{
    Task UpsertAsync(WorkflowRun run, CancellationToken ct = default);
    Task<WorkflowRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, string? workflowId, CancellationToken ct = default);
}

/// <summary>
/// Everything an executor needs to run one node: the tenant, the run's shared TraceId, a
/// pre-allocated SpanId for this node, the upstream node's SpanId (so the emitted span is a
/// real parent→child handoff edge), the node definition, and whether this is a dry-run
/// (deterministic, no network, no secret fetch, no egress).
/// </summary>
public sealed record NodeExecutionContext
{
    public required string TenantId { get; init; }
    public required string TraceId { get; init; }        // == RunId
    public required string SpanId { get; init; }         // pre-allocated for this node's span
    public string? ParentSpanId { get; init; }           // upstream node's SpanId (topological-first on fan-in)
    public required WorkflowNode Node { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>
/// The result of executing one node: its named outputs (fed to downstream inputs via edge
/// mappings), the canonical <see cref="Span"/> the engine enqueues on <see cref="IIngestChannel"/>
/// (so cost/budgets apply), and a non-PII output preview for the overlay.
/// </summary>
public sealed record NodeExecutionResult
{
    public required IReadOnlyDictionary<string, string> Outputs { get; init; }
    public required Span Span { get; init; }
    public string? OutputPreview { get; init; }
}

/// <summary>
/// Executes one workflow node. Pluggable per <see cref="WorkflowNodeType"/> so the Phase-12a
/// stubs and the Phase-12b real implementations satisfy the identical contract. Any executor
/// that makes an outbound call MUST assert egress first (fail closed under air-gap).
/// </summary>
public interface INodeExecutor
{
    /// <summary>The node type this executor handles (the engine dispatches on it).</summary>
    WorkflowNodeType Type { get; }

    Task<NodeExecutionResult> ExecuteAsync(
        NodeExecutionContext ctx,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken ct = default);
}
