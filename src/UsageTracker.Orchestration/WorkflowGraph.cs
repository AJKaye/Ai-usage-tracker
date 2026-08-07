using UsageTracker.Contracts;

namespace UsageTracker.Orchestration;

/// <summary>
/// Pure graph utilities over a <see cref="WorkflowDefinition"/> — no I/O, so they are
/// golden-testable and reused by both save-validation and the runner. A workflow must be a
/// DAG; cycles are rejected at save (<see cref="ValidateForSave"/>) and defensively re-checked
/// by <see cref="TopologicalOrder"/>.
/// </summary>
public static class WorkflowGraph
{
    /// <summary>
    /// Kahn's algorithm. Returns the node ids in a valid execution order, or null if the graph
    /// contains a cycle (fewer than N nodes get dequeued). Deterministic: ties broken by the
    /// node's original index so runs are reproducible.
    /// </summary>
    public static IReadOnlyList<string>? TopologicalOrder(WorkflowDefinition wf)
    {
        var indexOf = new Dictionary<string, int>();
        for (int i = 0; i < wf.Nodes.Count; i++) indexOf[wf.Nodes[i].Id] = i;

        var inDegree = wf.Nodes.ToDictionary(n => n.Id, _ => 0);
        var outEdges = wf.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var e in wf.Edges)
        {
            if (!inDegree.ContainsKey(e.FromNodeId) || !inDegree.ContainsKey(e.ToNodeId)) continue;
            outEdges[e.FromNodeId].Add(e.ToNodeId);
            inDegree[e.ToNodeId]++;
        }

        // Ready set as a sorted list keyed by original index → deterministic order.
        var ready = inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key)
            .OrderBy(id => indexOf[id]).ToList();
        var order = new List<string>(wf.Nodes.Count);

        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            order.Add(id);
            foreach (var next in outEdges[id])
            {
                if (--inDegree[next] == 0)
                {
                    // insert keeping ready sorted by original index
                    int pos = ready.FindIndex(r => indexOf[r] > indexOf[next]);
                    if (pos < 0) ready.Add(next); else ready.Insert(pos, next);
                }
            }
        }

        return order.Count == wf.Nodes.Count ? order : null;   // short → cycle
    }

    /// <summary>Incoming edges per node (upstream sources), in the definition's edge order.</summary>
    public static IReadOnlyDictionary<string, List<WorkflowEdge>> IncomingEdges(WorkflowDefinition wf)
    {
        var map = wf.Nodes.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
        foreach (var e in wf.Edges)
            if (map.TryGetValue(e.ToNodeId, out var list)) list.Add(e);
        return map;
    }

    /// <summary>
    /// Validates a definition for saving: unique non-empty node ids, edges reference existing
    /// nodes, and the graph is acyclic. Returns (true, null) if OK, else (false, reason).
    /// </summary>
    public static (bool Ok, string? Error) ValidateForSave(WorkflowDefinition wf)
    {
        if (string.IsNullOrWhiteSpace(wf.Name))
            return (false, "workflow name is required");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in wf.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id))
                return (false, "every node must have a non-empty id");
            if (!ids.Add(n.Id))
                return (false, $"duplicate node id '{n.Id}'");
        }

        foreach (var e in wf.Edges)
        {
            if (!ids.Contains(e.FromNodeId))
                return (false, $"edge references unknown source node '{e.FromNodeId}'");
            if (!ids.Contains(e.ToNodeId))
                return (false, $"edge references unknown target node '{e.ToNodeId}'");
            if (e.FromNodeId == e.ToNodeId)
                return (false, $"self-loop on node '{e.FromNodeId}' is not allowed");
        }

        if (TopologicalOrder(wf) is null)
            return (false, "workflow contains a cycle — must be a DAG");

        return (true, null);
    }
}
