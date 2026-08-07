using System.Collections.Concurrent;
using UsageTracker.Contracts;
using UsageTracker.Cost;
using UsageTracker.Orchestration;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 12 / workflow runner — topological execution, edge input-mapping, span emission with
/// binding metadata + parent→child handoffs, and the deterministic dry-run (which must NOT
/// enqueue any telemetry).
/// </summary>
public class WorkflowRunnerTests
{
    // Records every enqueued span so we can assert what the run emitted (and that dry-run emits nothing).
    private sealed class RecordingChannel : IIngestChannel
    {
        public ConcurrentQueue<Span> Enqueued { get; } = new();
        public Task EnqueueAsync(Span span, CancellationToken ct = default) { Enqueued.Enqueue(span); return Task.CompletedTask; }
    }

    private static ICostEngine Cost() => TieredCostEngine.CreateDefault(new PriceCatalog(OfflineBundleCatalogSource.Seed()));

    private static WorkflowRunner Runner(RecordingChannel channel, out InMemoryRunStore runs)
    {
        runs = new InMemoryRunStore();
        return new WorkflowRunner(
            new INodeExecutor[] { new TransformNodeExecutor(), new LlmNodeExecutor(), new HttpToolNodeExecutor(), new AgentLoopNodeExecutor() },
            channel, runs, Cost(), TimeProvider.System);
    }

    private static WorkflowNode Transform(string id, string op, Dictionary<string, string>? cfg = null,
        string[]? outputs = null) => new()
    {
        Id = id, Type = WorkflowNodeType.Transform, AgentName = $"agent-{id}", SkillName = $"skill-{id}",
        Config = new Dictionary<string, string>(cfg ?? new()) { ["op"] = op },
        OutputSchema = (outputs ?? new[] { "output" }).Select(n => new NodePort { Name = n }).ToList(),
    };

    [Fact]
    public async Task Two_transform_nodes_run_in_order_and_pass_output_to_input()
    {
        // a: template "{seed}!" → output; b: passthrough of a's output.
        var a = Transform("a", "template", new() { ["template"] = "{seed}!" });
        var b = Transform("b", "passthrough");
        var wf = new WorkflowDefinition
        {
            Id = "w", TenantId = "t", Name = "wf",
            Nodes = new[] { a, b }.ToList(),
            Edges = new[] { new WorkflowEdge { FromNodeId = "a", ToNodeId = "b" } }.ToList(),
        };

        var channel = new RecordingChannel();
        var runner = Runner(channel, out var runs);
        var run = await runner.RunAsync(wf, "run-1", new Dictionary<string, string> { ["seed"] = "hi" });

        Assert.Equal(RunState.Succeeded, run.State);
        Assert.All(run.Nodes, n => Assert.Equal(NodeStatus.Succeeded, n.Status));

        // Two spans emitted; b is a child of a (real handoff edge); both tagged agent+skill.
        var spans = channel.Enqueued.ToList();
        Assert.Equal(2, spans.Count);
        var sa = spans.Single(s => s.Metadata!["node"] == "a");
        var sb = spans.Single(s => s.Metadata!["node"] == "b");
        Assert.Equal("run-1", sa.TraceId);
        Assert.Equal("run-1", sb.TraceId);
        Assert.Null(sa.ParentSpanId);
        Assert.Equal(sa.SpanId, sb.ParentSpanId);          // handoff = parent→child
        Assert.Equal("agent-a", sa.Metadata!["agent"]);
        Assert.Equal("skill-b", sb.Metadata!["skill"]);
        Assert.Equal("w", sa.Metadata!["workflow"]);
    }

    [Fact]
    public async Task Edge_mapping_renames_output_to_downstream_input()
    {
        var a = Transform("a", "template", new() { ["template"] = "X" }, outputs: new[] { "result" });
        var b = Transform("b", "template", new() { ["template"] = "got:{incoming}" }, outputs: new[] { "final" });
        var wf = new WorkflowDefinition
        {
            Id = "w", TenantId = "t", Name = "wf",
            Nodes = new[] { a, b }.ToList(),
            Edges = new[]
            {
                new WorkflowEdge { FromNodeId = "a", ToNodeId = "b",
                    Mapping = new[] { new EdgeMapping("result", "incoming") }.ToList() },
            }.ToList(),
        };

        var channel = new RecordingChannel();
        var runner = Runner(channel, out var runs);
        var run = await runner.RunAsync(wf, "run-map", new Dictionary<string, string>());
        Assert.Equal(RunState.Succeeded, run.State);
        // b's output preview should reflect the mapped input "X" flowing into {incoming}.
        var b2 = run.Nodes.Single(n => n.NodeId == "b");
        Assert.Equal("got:X", b2.OutputPreview);
    }

    [Fact]
    public async Task Llm_node_emits_a_costed_span()
    {
        var node = new WorkflowNode
        {
            Id = "llm", Type = WorkflowNodeType.Llm, AgentName = "writer", SkillName = "draft",
            Config = new Dictionary<string, string> { ["model"] = "claude-opus-5", ["prompt"] = "write about {topic}" },
            OutputSchema = new[] { new NodePort { Name = "completion" } }.ToList(),
        };
        var wf = new WorkflowDefinition { Id = "w", TenantId = "t", Name = "wf", Nodes = new[] { node }.ToList() };

        var channel = new RecordingChannel();
        var runner = Runner(channel, out _);
        var run = await runner.RunAsync(wf, "run-llm", new Dictionary<string, string> { ["topic"] = "otters" });

        Assert.Equal(RunState.Succeeded, run.State);
        var span = channel.Enqueued.Single();
        Assert.Equal(SpanKind.Llm, span.Kind);
        Assert.Equal("anthropic", span.Provider);
        Assert.NotNull(span.Usage);
        Assert.True(span.Usage!.InputTokens > 0);
    }

    [Fact]
    public async Task Dry_run_projects_cost_and_enqueues_nothing()
    {
        var node = new WorkflowNode
        {
            Id = "llm", Type = WorkflowNodeType.Llm,
            Config = new Dictionary<string, string> { ["model"] = "claude-opus-5", ["prompt"] = "hello world prompt text" },
            OutputSchema = new[] { new NodePort { Name = "completion" } }.ToList(),
        };
        var wf = new WorkflowDefinition { Id = "w", TenantId = "t", Name = "wf", Nodes = new[] { node }.ToList() };

        var channel = new RecordingChannel();
        var runner = Runner(channel, out _);
        var projection = await runner.DryRunAsync(wf, new Dictionary<string, string>());

        Assert.Single(projection.Order);
        Assert.Equal("llm", projection.Order[0]);
        Assert.True(projection.SimulatedCost > 0m);         // a cataloged model → real projected cost
        Assert.True(channel.Enqueued.IsEmpty);              // dry-run must NOT pollute telemetry
    }

    [Fact]
    public async Task Dry_run_is_deterministic()
    {
        var node = Transform("t", "template", new() { ["template"] = "{x}" });
        var wf = new WorkflowDefinition { Id = "w", TenantId = "t", Name = "wf", Nodes = new[] { node }.ToList() };
        var runner = Runner(new RecordingChannel(), out _);

        var p1 = await runner.DryRunAsync(wf, new Dictionary<string, string> { ["x"] = "1" });
        var p2 = await runner.DryRunAsync(wf, new Dictionary<string, string> { ["x"] = "1" });
        Assert.Equal(p1.SimulatedCost, p2.SimulatedCost);
        Assert.Equal(p1.Order, p2.Order);
    }

    [Fact]
    public async Task Failed_upstream_skips_dependent_downstream()
    {
        // a requires an input it won't get → fails; b depends on a → skipped.
        var a = new WorkflowNode
        {
            Id = "a", Type = WorkflowNodeType.Transform,
            InputSchema = new[] { new NodePort { Name = "must", Required = true } }.ToList(),
            OutputSchema = new[] { new NodePort { Name = "output" } }.ToList(),
        };
        var b = Transform("b", "passthrough");
        var wf = new WorkflowDefinition
        {
            Id = "w", TenantId = "t", Name = "wf",
            Nodes = new[] { a, b }.ToList(),
            Edges = new[] { new WorkflowEdge { FromNodeId = "a", ToNodeId = "b" } }.ToList(),
        };

        var channel = new RecordingChannel();
        var runner = Runner(channel, out _);
        var run = await runner.RunAsync(wf, "run-fail", new Dictionary<string, string>());   // no "must"

        Assert.Equal(RunState.Failed, run.State);
        Assert.Equal(NodeStatus.Failed, run.Nodes.Single(n => n.NodeId == "a").Status);
        Assert.Equal(NodeStatus.Skipped, run.Nodes.Single(n => n.NodeId == "b").Status);
        Assert.True(channel.Enqueued.IsEmpty);              // neither node emitted
    }
}
