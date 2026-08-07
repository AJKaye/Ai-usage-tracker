using UsageTracker.Contracts;
using UsageTracker.Orchestration;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 12 / workflow builder — pure graph utilities: topological order, cycle detection,
/// and save-validation. Deterministic, no I/O.
/// </summary>
public class WorkflowGraphTests
{
    private static WorkflowNode Node(string id, WorkflowNodeType type = WorkflowNodeType.Transform) =>
        new() { Id = id, Type = type };

    private static WorkflowDefinition Wf(IEnumerable<WorkflowNode> nodes, IEnumerable<WorkflowEdge> edges) =>
        new() { Id = "w", TenantId = "t", Name = "wf", Nodes = nodes.ToList(), Edges = edges.ToList() };

    private static WorkflowEdge Edge(string from, string to) => new() { FromNodeId = from, ToNodeId = to };

    [Fact]
    public void Topological_order_respects_dependencies()
    {
        // a → b → d, a → c → d  (diamond / fan-out then fan-in)
        var wf = Wf(
            new[] { Node("a"), Node("b"), Node("c"), Node("d") },
            new[] { Edge("a", "b"), Edge("a", "c"), Edge("b", "d"), Edge("c", "d") });

        var order = WorkflowGraph.TopologicalOrder(wf)?.ToList();
        Assert.NotNull(order);
        // a before b,c; b,c before d
        Assert.True(order!.IndexOf("a") < order.IndexOf("b"));
        Assert.True(order.IndexOf("a") < order.IndexOf("c"));
        Assert.True(order.IndexOf("b") < order.IndexOf("d"));
        Assert.True(order.IndexOf("c") < order.IndexOf("d"));
    }

    [Fact]
    public void Cycle_returns_null_order_and_is_rejected_at_save()
    {
        var wf = Wf(
            new[] { Node("a"), Node("b"), Node("c") },
            new[] { Edge("a", "b"), Edge("b", "c"), Edge("c", "a") });   // cycle

        Assert.Null(WorkflowGraph.TopologicalOrder(wf));
        var (ok, err) = WorkflowGraph.ValidateForSave(wf);
        Assert.False(ok);
        Assert.Contains("cycle", err);
    }

    [Fact]
    public void Save_validation_rejects_dangling_edges_and_dupes()
    {
        var dangling = Wf(new[] { Node("a") }, new[] { Edge("a", "ghost") });
        Assert.False(WorkflowGraph.ValidateForSave(dangling).Ok);

        var dupe = Wf(new[] { Node("a"), Node("a") }, Array.Empty<WorkflowEdge>());
        Assert.False(WorkflowGraph.ValidateForSave(dupe).Ok);

        var selfLoop = Wf(new[] { Node("a") }, new[] { Edge("a", "a") });
        Assert.False(WorkflowGraph.ValidateForSave(selfLoop).Ok);
    }

    [Fact]
    public void Valid_dag_passes_save_validation()
    {
        var wf = Wf(new[] { Node("a"), Node("b") }, new[] { Edge("a", "b") });
        var (ok, err) = WorkflowGraph.ValidateForSave(wf);
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void Incoming_edges_are_grouped_per_target()
    {
        var wf = Wf(
            new[] { Node("a"), Node("b"), Node("c") },
            new[] { Edge("a", "c"), Edge("b", "c") });
        var incoming = WorkflowGraph.IncomingEdges(wf);
        Assert.Equal(2, incoming["c"].Count);
        Assert.Empty(incoming["a"]);
    }
}
