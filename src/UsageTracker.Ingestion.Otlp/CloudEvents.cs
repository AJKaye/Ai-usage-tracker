using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Ingestion.Otlp;

/// <summary>
/// Parses a CloudEvents 1.0 envelope into a <see cref="RawIngestEvent"/> of dialect
/// <c>"cloudevent"</c> (ARCHITECTURE.md §8.3 step 3 — the usage-event API for coarse
/// surfaces: RPA "AI units", IDE seats, premium requests). The event's <c>data</c>
/// object carries the coarse usage fields; envelope metadata (id/source/time) is
/// hoisted alongside so the mapper can build a canonical span.
///
/// <code>
/// { "specversion":"1.0", "type":"com.uipath.ai.units", "source":"orchestrator/robot-7",
///   "id":"evt-123", "time":"2026-08-05T10:00:00Z",
///   "data": { "provider":"uipath", "granularity":"credit",
///             "units_consumed":2, "unit_type":"ai_unit" } }
/// </code>
/// </summary>
public static class CloudEventParser
{
    public static RawIngestEvent Parse(string json, string tenantId, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("specversion", out _))
            throw new JsonException("not a CloudEvent: missing 'specversion'.");

        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Envelope metadata → attributes the mapper can read.
        if (root.TryGetProperty("id", out var id)) attrs["ce.id"] = id.GetString();
        if (root.TryGetProperty("type", out var type)) attrs["ce.type"] = type.GetString();
        if (root.TryGetProperty("source", out var src)) attrs["ce.source"] = src.GetString();
        DateTimeOffset? time = root.TryGetProperty("time", out var t) && t.GetString() is { } ts
            ? DateTimeOffset.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) : null;
        if (time is { } tv) attrs["ce.time"] = tv;

        // Flatten the data payload (JSON or a JSON string).
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(data.GetString()!);
                Flatten(inner.RootElement, attrs);
            }
            else
            {
                Flatten(data, attrs);
            }
        }

        return new RawIngestEvent
        {
            TenantId = tenantId,
            Dialect = "cloudevent",
            Attributes = attrs,
            ReceivedAt = time ?? receivedAt,
        };
    }

    private static void Flatten(JsonElement obj, Dictionary<string, object?> attrs)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in obj.EnumerateObject())
        {
            attrs[prop.Name] = ReadValue(prop.Value);
        }
    }

    // Preserve integer type: an integral JSON number must box as long (not double),
    // so downstream long-typed attributes (units_consumed) compare correctly. Each
    // arm is cast to object independently — a long/double ternary would coerce both
    // to double before boxing.
    private static object? ReadValue(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.Number:
                if (v.TryGetInt64(out var l)) return l;   // boxes as long
                return v.GetDouble();                     // boxes as double
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return v.ToString();
        }
    }
}

/// <summary>
/// Maps a CloudEvents coarse usage event to a canonical <see cref="Span"/>. Coarse
/// surfaces set <see cref="Span.Granularity"/> to credit/seat/request (never token),
/// so the cost engine prices them via the per-unit (CoarseUnit) path — no token math.
/// </summary>
public sealed class CloudEventMapper : ISpanMapper
{
    public bool Handles(string dialect) => dialect is "cloudevent";

    public Span Map(RawIngestEvent raw)
    {
        var a = raw.Attributes;
        var granularity = Attr.Str(a, "granularity")?.ToLowerInvariant() switch
        {
            "credit" => Granularity.Credit,
            "seat" => Granularity.Seat,
            "request" => Granularity.Request,
            "token" => Granularity.Token,
            _ => Granularity.Credit,   // a usage-event API is for coarse events by default
        };

        return new Span
        {
            TenantId = raw.TenantId,
            TraceId = Attr.Str(a, "trace_id") ?? Attr.Str(a, "ce.id") ?? Guid.NewGuid().ToString("n"),
            SpanId = Attr.Str(a, "span_id") ?? Attr.Str(a, "ce.id") ?? Guid.NewGuid().ToString("n"),
            Kind = SpanKind.Tool,
            Name = Attr.Str(a, "ce.type"),
            Provider = Attr.Str(a, "provider"),
            ResponseModel = Attr.Str(a, "model"),
            Granularity = granularity,
            UnitsConsumed = Attr.Long(a, "units_consumed"),
            UnitType = Attr.Str(a, "unit_type"),
            StartTime = raw.ReceivedAt,
            UserId = Attr.Str(a, "user_id"),
            Environment = Attr.Str(a, "environment"),
            Metadata = new Dictionary<string, string> { ["archetype"] = "usage-event", ["ce.source"] = Attr.Str(a, "ce.source") ?? "" },
        };
    }
}
