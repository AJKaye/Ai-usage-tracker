using System.Collections.Concurrent;
using System.Threading.Channels;
using UsageTracker.Contracts;
using UsageTracker.Orchestration;

namespace UsageTracker.Ingestion.Api;

/// <summary>A queued request to execute one workflow run.</summary>
public sealed record RunRequest(WorkflowDefinition Workflow, string RunId, IReadOnlyDictionary<string, string> InitialInputs);

/// <summary>
/// Drains a bounded in-process queue of workflow-run requests and executes each via
/// <see cref="WorkflowRunner"/> — mirroring <see cref="IngestConsumer"/> / <c>BudgetScanService</c>
/// (background, poison-isolated, cancellation-honoring). Lives in the API project (like
/// BudgetScanService) so <c>UsageTracker.Orchestration</c> stays a dependency-light library.
/// <c>POST /run</c> enqueues + returns the run id immediately (202); the heavy work happens
/// here, so the HTTP call stays fast and the run is truly async + cancelable. A per-run
/// <see cref="CancellationTokenSource"/> registry backs <c>POST /runs/{id}/cancel</c>.
/// </summary>
public sealed class WorkflowRunExecutorService : BackgroundService
{
    private readonly Channel<RunRequest> _queue =
        Channel.CreateBounded<RunRequest>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly IServiceProvider _sp;
    private readonly ILogger<WorkflowRunExecutorService> _log;

    public WorkflowRunExecutorService(IServiceProvider sp, ILogger<WorkflowRunExecutorService> log)
    {
        _sp = sp;
        _log = log;
    }

    /// <summary>Enqueue a run. Returns once accepted onto the queue (backpressure, never dropped).</summary>
    public ValueTask EnqueueAsync(RunRequest request, CancellationToken ct = default)
        => _queue.Writer.WriteAsync(request, ct);

    /// <summary>Request cancellation of an in-flight run. True if the run was found running.</summary>
    public bool Cancel(string runId)
    {
        if (_running.TryGetValue(runId, out var cts)) { cts.Cancel(); return true; }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[req.RunId] = linked;
            try
            {
                var runner = _sp.GetRequiredService<WorkflowRunner>();
                await runner.RunAsync(req.Workflow, req.RunId, req.InitialInputs, linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)   // a poison run must not kill the service
            {
                _log.LogError(ex, "workflow run {RunId} (workflow {WorkflowId}) failed in the executor loop",
                    req.RunId, req.Workflow.Id);
            }
            finally
            {
                _running.TryRemove(req.RunId, out _);
                linked.Dispose();
            }
        }
    }
}
