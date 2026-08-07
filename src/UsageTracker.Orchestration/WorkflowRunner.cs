using UsageTracker.Contracts;

namespace UsageTracker.Orchestration;

/// <summary>
/// Result of a dry-run — the expected execution order + a simulated cost projection, computed
/// deterministically with NO network, NO secret fetch, NO egress, and (critically) WITHOUT
/// enqueuing anything to the ingest channel (a dry-run must not pollute real telemetry).
/// </summary>
public sealed record DryRunProjection
{
    public required IReadOnlyList<string> Order { get; init; }
    public required decimal SimulatedCost { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<DryRunNode> PerNode { get; init; }
}

public sealed record DryRunNode(string NodeId, string Kind, decimal SimulatedCost, long InputTokens, long OutputTokens);

/// <summary>
/// Walks a workflow DAG in topological order and executes each node, threading a shared
/// TraceId + parent-span chain so handoffs are real parent→child spans, tagging each span with
/// Metadata[agent|skill|workflow|node], and enqueuing it on <see cref="IIngestChannel"/> so the
/// existing cost/allocation/budgets pipeline applies automatically. Per-node state is written to
/// <see cref="IRunStore"/> for the live overlay. Poison-isolated per node (a failing node halts
/// its dependency subtree, not independent branches), cancellation-honoring, and deterministic.
///
/// One RUN == one TraceId (== the run id). This is the whole design pivot: a run's telemetry is
/// exactly <c>SpanQuery{TraceId=runId}</c>.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly IReadOnlyDictionary<WorkflowNodeType, INodeExecutor> _executors;
    private readonly IIngestChannel _channel;
    private readonly IRunStore _runs;
    private readonly ICostEngine _cost;
    private readonly TimeProvider _clock;

    public WorkflowRunner(
        IEnumerable<INodeExecutor> executors,
        IIngestChannel channel,
        IRunStore runs,
        ICostEngine cost,
        TimeProvider clock)
    {
        _executors = executors.ToDictionary(e => e.Type);
        _channel = channel;
        _runs = runs;
        _cost = cost;
        _clock = clock;
    }

    /// <summary>
    /// Execute a workflow. Returns the terminal <see cref="WorkflowRun"/> state. Updates the run
    /// store as it progresses so the overlay sees live per-node status.
    /// </summary>
    public async Task<WorkflowRun> RunAsync(
        WorkflowDefinition wf, string runId, IReadOnlyDictionary<string, string> initialInputs, CancellationToken ct = default)
    {
        var order = WorkflowGraph.TopologicalOrder(wf)
            ?? throw new InvalidOperationException("workflow contains a cycle — cannot run");
        var nodeById = wf.Nodes.ToDictionary(n => n.Id);
        var incoming = WorkflowGraph.IncomingEdges(wf);

        // Live per-node state, seeded Pending.
        var state = wf.Nodes.ToDictionary(n => n.Id, n => new NodeRunState { NodeId = n.Id });
        var spanIdByNode = new Dictionary<string, string>();
        var outputsByNode = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var skipped = new HashSet<string>();

        var run = new WorkflowRun
        {
            RunId = runId,
            WorkflowId = wf.Id,
            TenantId = wf.TenantId,
            WorkflowVersion = wf.Version,
            State = RunState.Running,
            StartedAt = _clock.GetUtcNow(),
            Nodes = state.Values.ToList(),
        };
        await Save(run, state, ct);

        bool anyFailure = false;
        foreach (var nodeId in order)
        {
            if (ct.IsCancellationRequested)
            {
                run = run with { State = RunState.Canceled, EndedAt = _clock.GetUtcNow() };
                foreach (var s in state.Values.Where(s => s.Status is NodeStatus.Pending))
                    state[s.NodeId] = s with { Status = NodeStatus.Canceled };
                return await Save(run, state, ct);
            }

            var node = nodeById[nodeId];
            var upstream = incoming[nodeId];

            // Skip nodes whose any upstream failed/was skipped (dependency subtree halt).
            if (upstream.Any(e => skipped.Contains(e.FromNodeId) || state[e.FromNodeId].Status is NodeStatus.Failed))
            {
                skipped.Add(nodeId);
                state[nodeId] = state[nodeId] with { Status = NodeStatus.Skipped };
                await Save(run, state, ct);
                continue;
            }

            // Resolve inputs from upstream outputs via edge mappings (empty mapping = name-match).
            var inputs = ResolveInputs(node, upstream, outputsByNode, spanIdByNode, initialInputs, out var parentSpanId);

            var missing = node.InputSchema.Where(p => p.Required && !inputs.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            var spanId = Guid.NewGuid().ToString("n");
            spanIdByNode[nodeId] = spanId;

            state[nodeId] = state[nodeId] with { Status = NodeStatus.Running, StartedAt = _clock.GetUtcNow(), SpanId = spanId };
            await Save(run, state, ct);

            if (missing.Count > 0)
            {
                anyFailure = true;
                state[nodeId] = state[nodeId] with
                {
                    Status = NodeStatus.Failed,
                    EndedAt = _clock.GetUtcNow(),
                    Error = $"missing required input(s): {string.Join(", ", missing)}",
                };
                await Save(run, state, ct);
                continue;
            }

            if (!_executors.TryGetValue(node.Type, out var executor))
            {
                anyFailure = true;
                state[nodeId] = state[nodeId] with
                {
                    Status = NodeStatus.Failed, EndedAt = _clock.GetUtcNow(),
                    Error = $"no executor registered for node type '{node.Type}'",
                };
                await Save(run, state, ct);
                continue;
            }

            var ctx = new NodeExecutionContext
            {
                TenantId = wf.TenantId,
                TraceId = runId,
                SpanId = spanId,
                ParentSpanId = parentSpanId,
                Node = node,
                StartTime = _clock.GetUtcNow(),
                DryRun = false,
            };

            try
            {
                var result = await executor.ExecuteAsync(ctx, inputs, ct);
                var span = StampBinding(result.Span, ctx, wf, upstream, spanIdByNode);
                await _channel.EnqueueAsync(span, ct);      // cost/budgets/allocation apply for free
                outputsByNode[nodeId] = result.Outputs;
                state[nodeId] = state[nodeId] with
                {
                    Status = NodeStatus.Succeeded, EndedAt = _clock.GetUtcNow(), OutputPreview = result.OutputPreview,
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                run = run with { State = RunState.Canceled, EndedAt = _clock.GetUtcNow() };
                state[nodeId] = state[nodeId] with { Status = NodeStatus.Canceled, EndedAt = _clock.GetUtcNow() };
                return await Save(run, state, ct);
            }
            catch (Exception ex)   // poison-isolated per node
            {
                anyFailure = true;
                state[nodeId] = state[nodeId] with
                {
                    Status = NodeStatus.Failed, EndedAt = _clock.GetUtcNow(), Error = ex.Message,
                };
            }
            await Save(run, state, ct);
        }

        run = run with
        {
            State = anyFailure ? RunState.Failed : RunState.Succeeded,
            EndedAt = _clock.GetUtcNow(),
        };
        return await Save(run, state, ct);
    }

    /// <summary>
    /// Deterministic dry-run: walk the DAG, invoke each executor with DryRun=true, cost the
    /// simulated span with the injected engine, and return the order + projected cost. Enqueues
    /// NOTHING and touches NO store (pure projection).
    /// </summary>
    public async Task<DryRunProjection> DryRunAsync(
        WorkflowDefinition wf, IReadOnlyDictionary<string, string> initialInputs, CancellationToken ct = default)
    {
        var order = WorkflowGraph.TopologicalOrder(wf)
            ?? throw new InvalidOperationException("workflow contains a cycle — cannot dry-run");
        var nodeById = wf.Nodes.ToDictionary(n => n.Id);
        var incoming = WorkflowGraph.IncomingEdges(wf);
        var outputsByNode = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var spanIdByNode = new Dictionary<string, string>();

        var perNode = new List<DryRunNode>();
        decimal total = 0m;
        string currency = "USD";

        foreach (var nodeId in order)
        {
            var node = nodeById[nodeId];
            var upstream = incoming[nodeId];
            var inputs = ResolveInputs(node, upstream, outputsByNode, spanIdByNode, initialInputs, out var parentSpanId);
            var spanId = Guid.NewGuid().ToString("n");
            spanIdByNode[nodeId] = spanId;

            if (!_executors.TryGetValue(node.Type, out var executor)) continue;
            var ctx = new NodeExecutionContext
            {
                TenantId = wf.TenantId, TraceId = "dry-run", SpanId = spanId, ParentSpanId = parentSpanId,
                Node = node, StartTime = _clock.GetUtcNow(), DryRun = true,
            };
            var result = await executor.ExecuteAsync(ctx, inputs, ct);
            outputsByNode[nodeId] = result.Outputs;

            var cost = _cost.Cost(result.Span);
            decimal c = cost?.TotalCost ?? 0m;
            if (cost is { } cb) currency = cb.Currency;
            total += c;
            perNode.Add(new DryRunNode(nodeId, result.Span.Kind.ToString(), c,
                result.Span.Usage?.InputTokens ?? 0, result.Span.Usage?.OutputTokens ?? 0));
        }

        return new DryRunProjection { Order = order, SimulatedCost = total, Currency = currency, PerNode = perNode };
    }

    // Resolve a node's inputs from upstream outputs. parentSpanId = the first upstream's span
    // (the primary handoff edge); starts (no upstream) get initialInputs + null parent.
    private static IReadOnlyDictionary<string, string> ResolveInputs(
        WorkflowNode node,
        List<WorkflowEdge> upstream,
        Dictionary<string, IReadOnlyDictionary<string, string>> outputsByNode,
        Dictionary<string, string> spanIdByNode,
        IReadOnlyDictionary<string, string> initialInputs,
        out string? parentSpanId)
    {
        parentSpanId = null;
        if (upstream.Count == 0)
            return new Dictionary<string, string>(initialInputs);

        // Primary parent = first upstream (in edge order) that actually produced a span.
        foreach (var edge in upstream)
            if (spanIdByNode.TryGetValue(edge.FromNodeId, out var sid)) { parentSpanId = sid; break; }

        var inputs = new Dictionary<string, string>();
        foreach (var edge in upstream)
        {
            if (!outputsByNode.TryGetValue(edge.FromNodeId, out var up)) continue;   // upstream skipped
            if (edge.Mapping.Count == 0)
            {
                foreach (var (k, v) in up) inputs[k] = v;                            // name-match join
            }
            else
            {
                foreach (var m in edge.Mapping)
                    if (up.TryGetValue(m.FromOutput, out var v)) inputs[m.ToInput] = v;
            }
        }
        return inputs;
    }

    // Overlay identity-binding metadata onto the executor's span: agent/skill/workflow/node +
    // fan-in upstream span ids. Guarantees the trace tree + node binding are correct regardless
    // of what the executor set.
    private static Span StampBinding(Span span, NodeExecutionContext ctx, WorkflowDefinition wf,
        List<WorkflowEdge> upstream, Dictionary<string, string> spanIdByNode)
    {
        var meta = span.Metadata is { } m ? new Dictionary<string, string>(m) : new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(ctx.Node.AgentName)) meta["agent"] = ctx.Node.AgentName!;
        if (!string.IsNullOrEmpty(ctx.Node.SkillName)) meta["skill"] = ctx.Node.SkillName!;
        meta["workflow"] = wf.Id;
        meta["node"] = ctx.Node.Id;

        // Fan-in: record additional upstream span ids beyond the primary parent.
        var upstreamSpanIds = upstream
            .Select(e => spanIdByNode.GetValueOrDefault(e.FromNodeId))
            .Where(s => s is not null && s != ctx.ParentSpanId)
            .ToList();
        if (upstreamSpanIds.Count > 0) meta["upstream"] = string.Join(",", upstreamSpanIds);

        return span with
        {
            TenantId = ctx.TenantId, TraceId = ctx.TraceId, SpanId = ctx.SpanId, ParentSpanId = ctx.ParentSpanId,
            Metadata = meta,
        };
    }

    // Materialize the current node states into the run snapshot, persist it, and return it so
    // callers hold the up-to-date Nodes (not the stale seed on the outer `run`).
    private async Task<WorkflowRun> Save(WorkflowRun run, Dictionary<string, NodeRunState> state, CancellationToken ct)
    {
        var snapshot = run with { Nodes = state.Values.ToList() };
        await _runs.UpsertAsync(snapshot, ct);
        return snapshot;
    }
}
