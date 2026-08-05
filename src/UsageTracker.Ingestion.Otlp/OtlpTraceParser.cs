using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Ingestion.Otlp;

/// <summary>
/// Parses the OTLP/HTTP JSON <c>ExportTraceServiceRequest</c> envelope into one
/// <see cref="RawIngestEvent"/> per span. This is the REAL OpenTelemetry trace
/// wire shape — <c>resourceSpans[] → scopeSpans[] → spans[]</c>, each span's
/// <c>attributes[]</c> a list of <c>{key, value:{stringValue|intValue|doubleValue|boolValue}}</c>
/// (OTLP's AnyValue). Resource-level attributes are merged into every span so
/// resource-scoped keys (e.g. deployment.environment) are visible to the mapper.
///
/// Span identity: OTLP carries <c>traceId</c>/<c>spanId</c> as hex; we surface
/// them as the flat <c>trace_id</c>/<c>span_id</c> keys the mappers read.
/// Timestamps (<c>startTimeUnixNano</c>) are parsed when present.
///
/// Kept transport-agnostic (takes a JSON string/stream): the ASP.NET endpoint is
/// a thin caller, and the same parser serves a gRPC transport later.
/// </summary>
public static class OtlpTraceParser
{
    /// <summary>Flatten an OTLP JSON trace-export body into per-span raw events.</summary>
    public static IReadOnlyList<RawIngestEvent> Parse(string json, string tenantId, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var events = new List<RawIngestEvent>();

        if (!root.TryGetProperty("resourceSpans", out var resourceSpans) || resourceSpans.ValueKind != JsonValueKind.Array)
            return events;

        foreach (var rs in resourceSpans.EnumerateArray())
        {
            // resource-level attributes apply to every span underneath
            var resourceAttrs = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (rs.TryGetProperty("resource", out var resource) &&
                resource.TryGetProperty("attributes", out var rAttrs))
                ReadAttributes(rAttrs, resourceAttrs);

            if (!rs.TryGetProperty("scopeSpans", out var scopeSpans) || scopeSpans.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var ss in scopeSpans.EnumerateArray())
            {
                if (!ss.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var span in spans.EnumerateArray())
                {
                    var attrs = new Dictionary<string, object?>(resourceAttrs, StringComparer.Ordinal);

                    if (span.TryGetProperty("attributes", out var sAttrs))
                        ReadAttributes(sAttrs, attrs);

                    // hoist OTLP identity/name/time into the flat keys the mappers read
                    if (span.TryGetProperty("traceId", out var tid) && tid.ValueKind == JsonValueKind.String)
                        attrs["trace_id"] = tid.GetString();
                    if (span.TryGetProperty("spanId", out var sid) && sid.ValueKind == JsonValueKind.String)
                        attrs["span_id"] = sid.GetString();
                    if (span.TryGetProperty("parentSpanId", out var pid) && pid.ValueKind == JsonValueKind.String && pid.GetString() is { Length: > 0 } p)
                        attrs["parent_span_id"] = p;
                    if (span.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String && !attrs.ContainsKey(GenAi.OperationName))
                        attrs["span.name"] = nm.GetString();

                    var start = ParseUnixNano(span);

                    events.Add(new RawIngestEvent
                    {
                        TenantId = tenantId,
                        Dialect = "otlp.gen_ai",
                        Attributes = attrs,
                        ReceivedAt = start ?? receivedAt,
                    });
                }
            }
        }

        return events;
    }

    private static DateTimeOffset? ParseUnixNano(JsonElement span)
    {
        if (span.TryGetProperty("startTimeUnixNano", out var st))
        {
            long? nanos = st.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(st.GetString(), out var n) => n,
                JsonValueKind.Number when st.TryGetInt64(out var n) => n,
                _ => null,
            };
            if (nanos is > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(nanos.Value / 1_000_000);
        }
        return null;
    }

    /// <summary>Read an OTLP attributes array (list of {key,value:AnyValue}) into a flat bag.</summary>
    private static void ReadAttributes(JsonElement attributes, Dictionary<string, object?> into)
    {
        if (attributes.ValueKind != JsonValueKind.Array) return;
        foreach (var kv in attributes.EnumerateArray())
        {
            if (!kv.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
            var key = keyEl.GetString()!;
            if (!kv.TryGetProperty("value", out var val)) continue;
            into[key] = ReadAnyValue(val);
        }
    }

    /// <summary>Unwrap an OTLP AnyValue ({stringValue|intValue|doubleValue|boolValue|...}).</summary>
    private static object? ReadAnyValue(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object) return null;
        if (v.TryGetProperty("stringValue", out var s)) return s.GetString();
        if (v.TryGetProperty("intValue", out var i))
            return i.ValueKind == JsonValueKind.String && long.TryParse(i.GetString(), out var n) ? n
                 : i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out var n2) ? n2 : null;
        if (v.TryGetProperty("doubleValue", out var d) && d.TryGetDouble(out var dd)) return dd;
        if (v.TryGetProperty("boolValue", out var b)) return b.GetBoolean();
        return null; // arrayValue/kvlistValue not needed for the attrs we map
    }
}
