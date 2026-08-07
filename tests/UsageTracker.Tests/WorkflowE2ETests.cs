using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 12 end-to-end (workflow builder + execution): boots the real API and drives the full
/// path — create a workflow → dry-run (deterministic order + cost) → run it (the stub executors
/// emit costed spans, no network) → poll run state to terminal → assert spans carry agent+skill
/// metadata and cost shows in /v1/summary. Tenant isolation asserted.
/// </summary>
public class WorkflowE2ETests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public WorkflowE2ETests(EphemeralApiFactory factory) => _factory = factory;

    private HttpClient ClientFor(string tenant)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return c;
    }

    // A 3-node workflow: transform (build prompt) → llm (draft) → transform (finalize).
    private const string ThreeNodeWorkflow = """
    {
      "name": "draft-pipeline",
      "nodes": [
        { "id": "prep", "type": "transform", "agent_name": "prepper", "skill_name": "build-prompt",
          "config": { "op": "template", "template": "Topic: {topic}" },
          "outputs": [ { "name": "prompt" } ] },
        { "id": "write", "type": "llm", "agent_name": "writer", "skill_name": "draft",
          "config": { "model": "claude-opus-5", "prompt": "{prompt}" },
          "inputs": [ { "name": "prompt", "required": true } ],
          "outputs": [ { "name": "completion" } ] },
        { "id": "finish", "type": "transform", "agent_name": "finisher", "skill_name": "format",
          "config": { "op": "passthrough" },
          "outputs": [ { "name": "final" } ] }
      ],
      "edges": [
        { "from": "prep", "to": "write", "mapping": [ { "from_output": "prompt", "to_input": "prompt" } ] },
        { "from": "write", "to": "finish", "mapping": [ { "from_output": "completion", "to_input": "text" } ] }
      ]
    }
    """;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> PollRun(HttpClient client, string runId)
    {
        // The run executes on a background service; poll until terminal (or give up).
        for (int i = 0; i < 50; i++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/v1/runs/{runId}");
            var state = run.GetProperty("state").GetString();
            if (state is "Succeeded" or "Failed" or "Canceled") return run;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"run {runId} did not reach a terminal state in time");
    }

    [Fact]
    public async Task Create_dryrun_run_and_observe_cost()
    {
        var client = ClientFor("wf-e2e");

        // Create.
        var created = await client.PostAsync("/v1/workflows", Json(ThreeNodeWorkflow));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var wf = await created.Content.ReadFromJsonAsync<JsonElement>();
        var wfId = wf.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(wfId));

        // Dry-run: deterministic order prep → write → finish, projected cost > 0 (llm node).
        var dry = await client.PostAsync($"/v1/workflows/{wfId}/dry-run",
            Json("""{ "inputs": { "topic": "sea otters" } }"""));
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        var dryJson = await dry.Content.ReadFromJsonAsync<JsonElement>();
        var order = dryJson.GetProperty("order").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "prep", "write", "finish" }, order);
        Assert.True(dryJson.GetProperty("simulatedCost").GetDecimal() > 0m);

        // Run.
        var runResp = await client.PostAsync($"/v1/workflows/{wfId}/run",
            Json("""{ "inputs": { "topic": "sea otters" } }"""));
        Assert.Equal(HttpStatusCode.Accepted, runResp.StatusCode);
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetString()!;

        var run = await PollRun(client, runId);
        Assert.Equal("Succeeded", run.GetProperty("state").GetString());
        var nodes = run.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, n => Assert.Equal("Succeeded", n.GetProperty("status").GetString()));

        // Each node emitted a span under the run's trace; the llm span carries agent+skill.
        var writeNode = nodes.Single(n => n.GetProperty("nodeId").GetString() == "write");
        var spanId = writeNode.GetProperty("spanId").GetString();
        var span = await client.GetFromJsonAsync<JsonElement>($"/v1/spans/{spanId}");
        Assert.Equal(runId, span.GetProperty("traceId").GetString());
        var meta = span.GetProperty("metadata");
        Assert.Equal("writer", meta.GetProperty("agent").GetString());
        Assert.Equal("draft", meta.GetProperty("skill").GetString());

        // The run's spend rolls into the tenant summary (execution is a telemetry producer).
        var summary = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
        Assert.True(summary.GetProperty("spanCount").GetInt32() >= 3);
        Assert.True(summary.GetProperty("totalEstimatedCost").GetDecimal() > 0m);
    }

    [Fact]
    public async Task Runs_are_tenant_isolated()
    {
        var a = ClientFor("wf-iso-a");
        var b = ClientFor("wf-iso-b");

        var created = await a.PostAsync("/v1/workflows", Json(ThreeNodeWorkflow));
        var wfId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var runResp = await a.PostAsync($"/v1/workflows/{wfId}/run", Json("""{ "inputs": { "topic": "x" } }"""));
        var runId = (await runResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetString()!;
        await PollRun(a, runId);

        // Tenant B cannot see A's workflow or run.
        var bWorkflows = await b.GetFromJsonAsync<JsonElement>("/v1/workflows");
        Assert.Equal(0, bWorkflows.GetArrayLength());
        var bRun = await b.GetAsync($"/v1/runs/{runId}");
        Assert.Equal(HttpStatusCode.NotFound, bRun.StatusCode);
    }

    [Fact]
    public async Task Save_rejects_a_cycle()
    {
        var client = ClientFor("wf-cycle");
        var cyclic = """
        { "name": "loop", "nodes": [ { "id": "a", "type": "transform" }, { "id": "b", "type": "transform" } ],
          "edges": [ { "from": "a", "to": "b" }, { "from": "b", "to": "a" } ] }
        """;
        var resp = await client.PostAsync("/v1/workflows", Json(cyclic));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Cancel_unknown_run_is_404()
    {
        var client = ClientFor("wf-cancel");
        var resp = await client.PostAsync("/v1/runs/does-not-exist/cancel", Json("{}"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
