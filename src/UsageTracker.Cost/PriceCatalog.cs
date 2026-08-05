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
                InputPerToken = m.GetProperty("input_per_token").GetDecimal(),
                OutputPerToken = m.GetProperty("output_per_token").GetDecimal(),
                CacheReadPerToken = m.TryGetProperty("cache_read_per_token", out var cr) ? cr.GetDecimal() : 0m,
                CacheCreationPerToken = m.TryGetProperty("cache_creation_per_token", out var cc) ? cc.GetDecimal() : 0m,
                ReasoningPerToken = m.TryGetProperty("reasoning_per_token", out var rp) ? rp.GetDecimal() : null,
                CatalogVersion = version,
            });
        }
        return list;
    }

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
      ]
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
    private readonly Dictionary<string, ModelRate> _byModel;
    public string Version { get; }

    public PriceCatalog(IPriceCatalogSource source)
    {
        var rates = source.Load();
        _byModel = rates.ToDictionary(r => r.Model, StringComparer.OrdinalIgnoreCase);
        Version = rates.Count > 0 ? rates[0].CatalogVersion : "empty";
    }

    public ModelRate? Resolve(Span span)
    {
        var model = span.ResponseModel ?? span.RequestModel;
        if (model is null) return null;
        return _byModel.TryGetValue(model, out var rate) ? rate : null;
    }
}
