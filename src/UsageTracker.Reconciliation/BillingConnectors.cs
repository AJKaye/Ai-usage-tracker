using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Reconciliation;

/// <summary>
/// Pulls realized (billed) USD from OpenAI's Costs API
/// (<c>GET /v1/organization/costs</c>; ARCHITECTURE.md §4.3/§10). The
/// <see cref="HttpClient"/> is INJECTED so tests feed canned responses — no real
/// network. The API key is resolved from <see cref="ISecretProvider"/> by name,
/// never from config/code (PROJECT_CONTEXT §6).
///
/// Response shape (buckets of daily results):
/// <code>
/// { "data": [ { "start_time": 1..., "results": [
///     { "amount": { "value": 12.34, "currency": "usd" }, "line_item": "gpt-5.6" } ] } ] }
/// </code>
/// </summary>
public sealed class OpenAiBillingConnector : IBillingConnector
{
    private readonly HttpClient _http;
    private readonly ISecretProvider _secrets;
    private readonly string _secretNameTemplate;
    private readonly IEgressGuard? _egress;
    public string Provider => "openai";

    // The secret name is a TEMPLATE that may contain "{tenant}", so pooled SaaS
    // resolves each tenant's OWN billing credential (no cross-tenant realized-cost
    // bleed). Self-host uses a literal name (tenant count = 1). Default is
    // tenant-scoped; pass a literal name for self-host if preferred.
    public OpenAiBillingConnector(HttpClient http, ISecretProvider secrets, string secretNameTemplate = "openai.admin_key.{tenant}", IEgressGuard? egress = null)
        => (_http, _secrets, _secretNameTemplate, _egress) = (http, secrets, secretNameTemplate, egress);

    public async Task<IReadOnlyList<RealizedCost>> PullAsync(string tenantId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // Air-gap gate: realized-cost pull is an outbound call — fail closed at the site.
        _egress?.AssertEgressAllowed("api.openai.com", "OpenAI Costs pull");
        var secretName = _secretNameTemplate.Replace("{tenant}", tenantId);
        var key = await _secrets.GetAsync(secretName, ct)
            ?? throw new InvalidOperationException($"secret '{secretName}' not resolved — cannot pull OpenAI costs for tenant '{tenantId}'.");

        long start = ToUnix(from);
        long end = ToUnix(to.AddDays(1));   // exclusive end of the last day
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/organization/costs?start_time={start}&end_time={end}&bucket_width=1d");
        req.Headers.Add("Authorization", $"Bearer {key}");

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var rows = new List<RealizedCost>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var buckets)) return rows;
        foreach (var bucket in buckets.EnumerateArray())
        {
            DateOnly day = bucket.TryGetProperty("start_time", out var st)
                ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(st.GetInt64()).UtcDateTime)
                : from;
            if (!bucket.TryGetProperty("results", out var results)) continue;
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("amount", out var amt)) continue;
                rows.Add(new RealizedCost
                {
                    Provider = Provider,
                    Day = day,
                    Model = r.TryGetProperty("line_item", out var li) ? li.GetString() : null,
                    Amount = amt.GetProperty("value").GetDecimal(),
                    Currency = (amt.TryGetProperty("currency", out var c) ? c.GetString() : "usd")!.ToUpperInvariant(),
                    CostType = "tokens",
                });
            }
        }
        return rows;
    }

    private static long ToUnix(DateOnly d) =>
        new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
}

/// <summary>
/// Pulls realized USD from Anthropic's cost_report
/// (<c>GET /v1/organizations/cost_report</c>; ARCHITECTURE.md §4.3/§10).
/// <b>Caveat (§5 #12):</b> cost_report EXCLUDES Priority Tier spend — so the
/// realized total here can understate true billed cost for orgs using Priority
/// Tier. Rows are flagged (<see cref="RealizedCost.CostType"/>) and the connector
/// exposes <see cref="ExcludesPriorityTier"/> so the reconciler/UI can surface it.
/// Injected <see cref="HttpClient"/> (no real network in tests); key via
/// <see cref="ISecretProvider"/>.
///
/// Response shape:
/// <code>
/// { "data": [ { "starting_at": "2026-08-05T00:00:00Z",
///     "results": [ { "amount": "12.34", "currency": "USD", "model": "claude-opus-5" } ] } ] }
/// </code>
/// </summary>
public sealed class AnthropicBillingConnector : IBillingConnector
{
    private readonly HttpClient _http;
    private readonly ISecretProvider _secrets;
    private readonly string _secretNameTemplate;
    public string Provider => "anthropic";

    /// <summary>cost_report omits Priority Tier — a documented understatement risk.</summary>
    public bool ExcludesPriorityTier => true;

    private readonly IEgressGuard? _egress;

    // Tenant-scoped secret template ("{tenant}" placeholder) — see OpenAiBillingConnector.
    public AnthropicBillingConnector(HttpClient http, ISecretProvider secrets, string secretNameTemplate = "anthropic.admin_key.{tenant}", IEgressGuard? egress = null)
        => (_http, _secrets, _secretNameTemplate, _egress) = (http, secrets, secretNameTemplate, egress);

    public async Task<IReadOnlyList<RealizedCost>> PullAsync(string tenantId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        _egress?.AssertEgressAllowed("api.anthropic.com", "Anthropic cost_report pull");
        var secretName = _secretNameTemplate.Replace("{tenant}", tenantId);
        var key = await _secrets.GetAsync(secretName, ct)
            ?? throw new InvalidOperationException($"secret '{secretName}' not resolved — cannot pull Anthropic cost_report for tenant '{tenantId}'.");

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/organizations/cost_report?starting_at={from:yyyy-MM-dd}&ending_at={to.AddDays(1):yyyy-MM-dd}");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        var rows = new List<RealizedCost>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var buckets)) return rows;
        foreach (var bucket in buckets.EnumerateArray())
        {
            DateOnly day = bucket.TryGetProperty("starting_at", out var sa) && sa.GetString() is { } s
                ? DateOnly.FromDateTime(DateTimeOffset.Parse(s).UtcDateTime)
                : from;
            if (!bucket.TryGetProperty("results", out var results)) continue;
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("amount", out var amt)) continue;
                // amount may be a JSON string ("12.34") or number — accept both.
                decimal amount = amt.ValueKind == JsonValueKind.String
                    ? decimal.Parse(amt.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : amt.GetDecimal();
                rows.Add(new RealizedCost
                {
                    Provider = Provider,
                    Day = day,
                    Model = r.TryGetProperty("model", out var m) ? m.GetString() : null,
                    Amount = amount,
                    Currency = (r.TryGetProperty("currency", out var c) ? c.GetString() : "USD")!.ToUpperInvariant(),
                    CostType = "tokens (excl. priority tier)",
                });
            }
        }
        return rows;
    }
}
