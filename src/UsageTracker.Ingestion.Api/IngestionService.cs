using UsageTracker.Contracts;
using UsageTracker.Normalization;

namespace UsageTracker.Ingestion.Api;

/// <summary>
/// The ingest pipeline: DTO → canonical Span → normalize tokens → estimate cost →
/// persist. This is the composition point where the (independently replaceable)
/// normalizer, cost engine, and event store are wired together. Kept off the HTTP
/// layer so it is unit-testable and reusable by other ingestion archetypes
/// (proxy, batch pull) later.
/// </summary>
public sealed class IngestionService
{
    private readonly TokenNormalizerRegistry _normalizers;
    private readonly ICostEngine _costEngine;
    private readonly IEventStore _store;
    private readonly TimeProvider _clock;

    public IngestionService(
        TokenNormalizerRegistry normalizers,
        ICostEngine costEngine,
        IEventStore store,
        TimeProvider clock)
    {
        _normalizers = normalizers;
        _costEngine = costEngine;
        _store = store;
        _clock = clock;
    }

    public async Task<Span> IngestAsync(string tenantId, IngestEventDto dto, CancellationToken ct = default)
    {
        var granularity = IngestEventDto.ParseGranularity(dto.Granularity);

        // Only token-metered events get token normalization + token-based cost.
        NormalizedUsage? usage = null;
        if (granularity == Granularity.Token)
            usage = _normalizers.Normalize(dto.Provider, dto.ToRawUsage());

        var now = _clock.GetUtcNow();
        var span = new Span
        {
            TenantId = tenantId,
            TraceId = dto.TraceId ?? Guid.NewGuid().ToString("n"),
            SpanId = dto.SpanId ?? Guid.NewGuid().ToString("n"),
            ParentSpanId = dto.ParentSpanId,
            SessionId = dto.SessionId,
            Kind = IngestEventDto.ParseKind(dto.Kind),
            Name = dto.Operation,
            Provider = dto.Provider,
            RequestModel = dto.RequestModel,
            ResponseModel = dto.ResponseModel,
            TokenizerId = dto.Tokenizer,
            ServiceTier = dto.ServiceTier,
            IsBatch = dto.IsBatch,
            Region = dto.Region,
            DeploymentType = dto.DeploymentType,
            ToolCalls = dto.ToToolCalls(),
            Granularity = granularity,
            RawUsage = granularity == Granularity.Token ? dto.ToRawUsage() : null,
            Usage = usage,
            UnitsConsumed = dto.UnitsConsumed,
            UnitType = dto.UnitType,
            StartTime = dto.StartTime ?? now,
            EndTime = dto.EndTime,
            TimeToFirstTokenMs = dto.TimeToFirstTokenMs,
            UserId = dto.UserId,
            TeamId = dto.TeamId,
            Environment = dto.Environment,
            Metadata = dto.Metadata,
        };

        // Estimate cost (the 3-tier engine), then attach.
        var cost = _costEngine.Cost(span);
        span = span with { EstimatedCost = cost };

        await _store.AppendAsync(span, ct);
        return span;
    }
}
