using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Cost;

/// <summary>
/// Offline, signed-bundle catalog source (ARCHITECTURE.md §4.3). Loads rates from
/// a JSON file — the air-gap/FedRAMP path (D6). A live-sync source implementing
/// the same <see cref="IPriceCatalogSource"/> drops in without touching callers.
/// Rates are per-token USD. Seed values are representative 2026 figures; the
/// catalog is date-stamped so historical recompute stays stable.
/// </summary>
public sealed class OfflineBundleCatalogSource : IPriceCatalogSource
{
    private readonly string _json;
    public string SourceId => "offline-bundle";

    public OfflineBundleCatalogSource(string json) => _json = json;

    public static OfflineBundleCatalogSource FromFile(string path) =>
        new(File.ReadAllText(path));

    /// <summary>The built-in seed bundle so the slice runs with no external file.</summary>
    public static OfflineBundleCatalogSource Seed() => new(SeedJson);

    public IReadOnlyList<ModelRate> Load()
    {
        using var doc = JsonDocument.Parse(_json);
        string version = doc.RootElement.GetProperty("version").GetString()!;
        var list = new List<ModelRate>();
        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            list.Add(new ModelRate
            {
                Model = m.GetProperty("model").GetString()!,
                Currency = m.TryGetProperty("currency", out var c) ? c.GetString()! : "USD",
                // Token prices are optional: a per_hour (PTU) or per_unit rate has none.
                InputPerToken = m.TryGetProperty("input_per_token", out var ip) ? ip.GetDecimal() : 0m,
                OutputPerToken = m.TryGetProperty("output_per_token", out var op) ? op.GetDecimal() : 0m,
                CacheReadPerToken = m.TryGetProperty("cache_read_per_token", out var cr) ? cr.GetDecimal() : 0m,
                CacheCreationPerToken = m.TryGetProperty("cache_creation_per_token", out var cc) ? cc.GetDecimal() : 0m,
                ReasoningPerToken = m.TryGetProperty("reasoning_per_token", out var rp) ? rp.GetDecimal() : null,
                CatalogVersion = version,
                // provenance for the per-event rate snapshot (Increment 1)
                SourceId = SourceId,
                EffectiveFrom = m.TryGetProperty("effective_from", out var ef) && ef.GetString() is { } efs
                    ? DateOnly.Parse(efs, System.Globalization.CultureInfo.InvariantCulture) : null,
                EffectiveTo = m.TryGetProperty("effective_to", out var et) && et.GetString() is { } ets
                    ? DateOnly.Parse(ets, System.Globalization.CultureInfo.InvariantCulture) : null,
                // composite-key selectors (Increment 3); absent → wildcard defaults
                // pricing mode + additive rates (Increment 4)
                Mode = m.TryGetProperty("mode", out var md) && md.GetString() is { } mds
                    ? ParseRateMode(mds) : PricingMode.PerToken,
                Multiplier = m.TryGetProperty("multiplier", out var mul) ? mul.GetDecimal() : 1.0m,
                AudioPerToken = m.TryGetProperty("audio_per_token", out var au) ? au.GetDecimal() : null,
                ImagePerToken = m.TryGetProperty("image_per_token", out var im) ? im.GetDecimal() : null,
                HourlyRate = m.TryGetProperty("hourly_rate", out var hr) ? hr.GetDecimal() : null,
                ContextTier = m.TryGetProperty("context_tier", out var ctx) && ctx.GetString() is { } cts
                    ? Enum.Parse<ContextTier>(cts, ignoreCase: true) : ContextTier.Any,
                LongContextThresholdTokens = m.TryGetProperty("long_context_threshold", out var lct)
                    ? lct.GetInt64() : null,
                IsBatch = m.TryGetProperty("is_batch", out var ib) && ib.GetBoolean(),
                ServiceTier = m.TryGetProperty("service_tier", out var st) ? st.GetString() : null,
                Region = m.TryGetProperty("region", out var rg) ? rg.GetString() : null,
                DeploymentType = m.TryGetProperty("deployment_type", out var dt) ? dt.GetString() : null,
            });
        }
        return list;
    }

    /// <summary>Per-unit rates (credits/seats/requests) from the same bundle. Absent
    /// section → empty, so token-only bundles are unaffected (ARCHITECTURE.md §5 #15).</summary>
    public IReadOnlyList<UnitRate> LoadUnits()
    {
        using var doc = JsonDocument.Parse(_json);
        string version = doc.RootElement.GetProperty("version").GetString()!;
        var list = new List<UnitRate>();
        if (!doc.RootElement.TryGetProperty("unit_rates", out var arr)) return list;
        foreach (var u in arr.EnumerateArray())
        {
            list.Add(new UnitRate
            {
                UnitType = u.GetProperty("unit_type").GetString()!,
                Mode = ParseMode(u.GetProperty("mode").GetString()!),
                PricePerUnit = u.GetProperty("price_per_unit").GetDecimal(),
                Currency = u.TryGetProperty("currency", out var c) ? c.GetString()! : "USD",
                Provider = u.TryGetProperty("provider", out var p) ? p.GetString() : null,
                Model = u.TryGetProperty("model", out var m) ? m.GetString() : null,
                CatalogVersion = version,
            });
        }
        return list;

        static PricingMode ParseMode(string s) => s switch
        {
            "per_unit" => PricingMode.PerUnit,
            "per_request" => PricingMode.PerRequest,
            "per_seat" => PricingMode.PerSeat,
            "per_hour" => PricingMode.PerHour,
            _ => PricingMode.PerToken,
        };
    }

    /// <summary>Per-call tool surcharges (tool type → USD/call) from the bundle
    /// (ARCHITECTURE.md §5 #7). Absent section → empty.</summary>
    public IReadOnlyDictionary<string, decimal> LoadToolSurcharges()
    {
        using var doc = JsonDocument.Parse(_json);
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("tool_surcharges", out var obj)) return map;
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = prop.Value.GetDecimal();
        return map;
    }

    // Rate metering mode (shared with ModelRate parse).
    private static PricingMode ParseRateMode(string s) => s switch
    {
        "per_hour" => PricingMode.PerHour,
        "per_unit" => PricingMode.PerUnit,
        "per_request" => PricingMode.PerRequest,
        "per_seat" => PricingMode.PerSeat,
        _ => PricingMode.PerToken,
    };

    // Seed rates: per-token USD (list price / 1e6). Representative, date-stamped.
    private const string SeedJson = """
    {
      "version": "seed-2026-08-04",
      "models": [
        { "model": "claude-opus-5",     "input_per_token": 0.000005,  "output_per_token": 0.000025, "cache_read_per_token": 0.0000005, "cache_creation_per_token": 0.00000625 },
        { "model": "claude-opus-4-8",   "input_per_token": 0.000005,  "output_per_token": 0.000025, "cache_read_per_token": 0.0000005, "cache_creation_per_token": 0.00000625 },
        { "model": "claude-sonnet-5",   "input_per_token": 0.000003,  "output_per_token": 0.000015, "cache_read_per_token": 0.0000003, "cache_creation_per_token": 0.00000375 },
        { "model": "gpt-5.6",           "input_per_token": 0.000005,  "output_per_token": 0.000015, "cache_read_per_token": 0.0000005, "cache_creation_per_token": 0.00000625 },
        { "model": "gemini-3.1-pro",    "input_per_token": 0.000002,  "output_per_token": 0.000012, "cache_read_per_token": 0.0000002, "cache_creation_per_token": 0.0 }
      ],
      "unit_rates": [
        { "unit_type": "ai_unit",         "provider": "uipath", "mode": "per_unit",    "price_per_unit": 0.20 },
        { "unit_type": "premium_request", "provider": "github", "mode": "per_request", "price_per_unit": 0.04 },
        { "unit_type": "premium_request", "provider": "github", "model": "gpt-5.6-max", "mode": "per_request", "price_per_unit": 0.20 },
        { "unit_type": "copilot_seat",    "provider": "github", "mode": "per_seat",    "price_per_unit": 19.00 }
      ],
      "tool_surcharges": {
        "web_search": 0.01,
        "code_interpreter": 0.03,
        "file_search": 0.0025
      }
    }
    """;
}

/// <summary>
/// In-memory catalog built from a source. Keys on model for this slice; the
/// <see cref="Resolve"/> signature takes the whole <see cref="Span"/> so richer
/// composite keying (service tier / region / batch) is a non-breaking extension.
/// </summary>
public sealed class PriceCatalog : IPriceCatalog
{
    // Multiple rate VARIANTS per model (base + batch + long-context + region/tier/…).
    // Resolution picks the most-specific variant whose date window contains the event.
    private readonly ILookup<string, ModelRate> _byModel;
    private readonly IReadOnlyList<UnitRate> _units;   // small; linear scan with most-specific-wins
    private readonly IReadOnlyDictionary<string, decimal> _toolSurcharges;
    public string Version { get; }

    public PriceCatalog(IPriceCatalogSource source)
    {
        var rates = source.Load();
        _byModel = rates.ToLookup(r => r.Model, StringComparer.OrdinalIgnoreCase);
        _units = source.LoadUnits();
        _toolSurcharges = source.LoadToolSurcharges();
        Version = rates.Count > 0 ? rates[0].CatalogVersion : "empty";
    }

    public decimal? ToolSurcharge(string toolType)
        => _toolSurcharges.TryGetValue(toolType, out var v) ? v : null;

    public ModelRate? Resolve(Span span)
    {
        var model = span.ResponseModel ?? span.RequestModel;
        if (model is null) return null;

        var when = DateOnly.FromDateTime(span.StartTime.UtcDateTime);
        long inputTokens = span.Usage?.InputTokens ?? 0;

        ModelRate? best = null;
        int bestScore = -1;
        foreach (var rate in _byModel[model])
        {
            // Date-effective window (#14): the rate must be in effect at the event time.
            if (rate.EffectiveFrom is { } from && when < from) continue;
            if (rate.EffectiveTo is { } to && when >= to) continue;

            // Context tier (#5): a Long/Standard variant must match the request's size.
            // Which tier the request is in is decided by whichever variant declares the
            // threshold (base rows carry LongContextThresholdTokens; Long rows re-rate above it).
            if (rate.ContextTier != ContextTier.Any)
            {
                long threshold = rate.LongContextThresholdTokens ?? ThresholdFor(model);
                bool isLong = threshold > 0 && inputTokens > threshold;
                if (rate.ContextTier == ContextTier.Long && !isLong) continue;
                if (rate.ContextTier == ContextTier.Standard && isLong) continue;
            }

            // Each non-wildcard dim on the rate must equal the span's; null/false = wildcard.
            if (!DimMatches(rate.IsBatch, span.IsBatch)) continue;
            if (!StrMatches(rate.ServiceTier, span.ServiceTier)) continue;
            if (!StrMatches(rate.Region, span.Region)) continue;
            if (!StrMatches(rate.DeploymentType, span.DeploymentType)) continue;

            int score = Specificity(rate);
            // Ties broken by the newest effective date, so a later promo wins.
            if (score > bestScore ||
                (score == bestScore && Newer(rate.EffectiveFrom, best?.EffectiveFrom)))
            {
                best = rate;
                bestScore = score;
            }
        }
        return best;

        // A base row's threshold is what tells a Long variant where "long" begins.
        long ThresholdFor(string m) => _byModel[m]
            .Select(r => r.LongContextThresholdTokens ?? 0).DefaultIfEmpty(0).Max();
    }

    // A dim declared on the rate constrains matching; a wildcard (false/null) matches anything.
    private static bool DimMatches(bool rateIsBatch, bool? spanIsBatch)
        => !rateIsBatch || spanIsBatch == true;

    private static bool StrMatches(string? rateDim, string? spanDim)
        => rateDim is null || string.Equals(rateDim, spanDim, StringComparison.OrdinalIgnoreCase);

    private static int Specificity(ModelRate r)
    {
        int n = 0;
        if (r.IsBatch) n++;
        if (r.ContextTier != ContextTier.Any) n++;
        if (r.ServiceTier is not null) n++;
        if (r.Region is not null) n++;
        if (r.DeploymentType is not null) n++;
        return n;
    }

    private static bool Newer(DateOnly? a, DateOnly? b)
        => (a ?? DateOnly.MinValue) > (b ?? DateOnly.MinValue);

    // Most-specific-wins: (provider, model, unit) → (provider, unit) → (unit).
    public UnitRate? ResolveUnit(Span span)
    {
        if (span.UnitType is not { } ut) return null;
        var model = span.ResponseModel ?? span.RequestModel;
        return Match(ut, span.Provider, model)
            ?? Match(ut, span.Provider, null)
            ?? Match(ut, null, null);

        UnitRate? Match(string unit, string? prov, string? mdl) => _units.FirstOrDefault(r =>
            string.Equals(r.UnitType, unit, StringComparison.OrdinalIgnoreCase)
            && (prov is null ? r.Provider is null : string.Equals(r.Provider, prov, StringComparison.OrdinalIgnoreCase))
            && (mdl is null ? r.Model is null : string.Equals(r.Model, mdl, StringComparison.OrdinalIgnoreCase)));
    }
}
