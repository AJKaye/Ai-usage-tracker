using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Security-wiring: proves the tamper-evident audit sink is now EXERCISED by the
/// request path (not just a tested library). A mutating /v1 request produces a
/// verifiable audit-chain entry, tenant-scoped, exportable via GET /v1/audit.
/// </summary>
public class AuditEmissionTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public AuditEmissionTests(EphemeralApiFactory factory) => _factory = factory;

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Mutating_request_writes_a_verifiable_audit_entry()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "audit-t");

        // A mutating call.
        var ingest = await client.PostAsync("/v1/ingest", Json(
            """{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5","gen_ai.usage.input_tokens":100,"gen_ai.usage.output_tokens":50,"span_id":"aud-1","kind":"llm"}"""));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var audit = await client.GetFromJsonAsync<JsonElement>("/v1/audit");
        Assert.True(audit.GetProperty("intact").GetBoolean());           // chain verifies
        var entries = audit.GetProperty("entries");
        Assert.True(entries.GetArrayLength() >= 1, "expected an audit entry for the ingest");
        // the recorded action names the mutating route
        Assert.Contains(entries.EnumerateArray(),
            e => e.GetProperty("event").GetProperty("action").GetString()!.Contains("/v1/ingest"));
    }

    [Fact]
    public async Task Audit_is_tenant_scoped()
    {
        var a = _factory.CreateClient(); a.DefaultRequestHeaders.Add("X-Tenant-Id", "aud-a");
        await a.PostAsync("/v1/ingest", Json(
            """{"gen_ai.provider.name":"openai","gen_ai.response.model":"gpt-5.6","gen_ai.usage.input_tokens":10,"gen_ai.usage.output_tokens":5,"span_id":"a1","kind":"llm"}"""));

        var b = _factory.CreateClient(); b.DefaultRequestHeaders.Add("X-Tenant-Id", "aud-b");
        var bAudit = await b.GetFromJsonAsync<JsonElement>("/v1/audit");
        Assert.Equal(0, bAudit.GetProperty("entries").GetArrayLength());   // no cross-tenant audit bleed
    }

    [Fact]
    public async Task Read_only_requests_are_not_audited()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "aud-ro");
        await client.GetFromJsonAsync<JsonElement>("/v1/summary");   // a GET
        var audit = await client.GetFromJsonAsync<JsonElement>("/v1/audit");
        Assert.Equal(0, audit.GetProperty("entries").GetArrayLength());   // GETs don't pollute the chain
    }
}
