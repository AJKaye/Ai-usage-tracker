using System.Threading.Channels;
using UsageTracker.Contracts;

namespace UsageTracker.Ingestion.Api;

/// <summary>
/// In-process <see cref="IIngestChannel"/> over <see cref="System.Threading.Channels"/>.
/// The hot path (receive → validate → enqueue) returns as soon as the span is
/// accepted here; normalize/cost/store happen on the background consumer. This is
/// the SLO seam (§8: accept in &lt;10ms p99, heavy work async) and the exact seam a
/// Kafka <see cref="IStreamBus"/>-backed channel slots behind in the distributed
/// tier — the API code above it does not change.
///
/// Bounded + DropWrite-free: writes wait when full (backpressure) rather than
/// dropping, so a burst slows the producer instead of losing events. Dedup +
/// durability are the store's job (idempotent by (tenant, span)).
/// </summary>
public sealed class ChannelIngest : IIngestChannel
{
    private readonly Channel<Span> _channel;

    public ChannelIngest(int capacity = 10_000)
    {
        _channel = Channel.CreateBounded<Span>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,     // backpressure, never silent drop
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public async Task EnqueueAsync(Span span, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(span, ct);

    /// <summary>Consumed by the background service.</summary>
    internal ChannelReader<Span> Reader => _channel.Reader;
}

/// <summary>
/// Drains <see cref="ChannelIngest"/> and runs the downstream pipeline
/// (normalize is already done at map time; here we cost + persist). One reader;
/// scale-out in the distributed tier is more consumers on the Kafka topic.
/// </summary>
public sealed class IngestConsumer : BackgroundService
{
    private readonly ChannelIngest _channel;
    private readonly ICostEngine _cost;
    private readonly IEventStore _store;
    private readonly ILogger<IngestConsumer> _log;

    public IngestConsumer(ChannelIngest channel, ICostEngine cost, IEventStore store, ILogger<IngestConsumer> log)
    {
        _channel = channel;
        _cost = cost;
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var span in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var costed = span with { EstimatedCost = _cost.Cost(span) };
                await _store.AppendAsync(costed, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poison event must not kill the consumer. In the distributed
                // tier this is a Kafka dead-letter; here we log and continue.
                _log.LogError(ex, "ingest consumer failed to persist span {SpanId} (tenant {Tenant})",
                    span.SpanId, span.TenantId);
            }
        }
    }
}
