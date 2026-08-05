using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using UsageTracker.Contracts;
using UsageTracker.Reconciliation;
using UsageTracker.Security;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 7 / Increment 3 — API auth enforcement end-to-end. A factory with API keys
/// configured: an authenticated request is scoped to the KEY's tenant (a client
/// cannot assert a different tenant via header), a presented-but-invalid credential
/// is 401, and /health stays open.
/// </summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Profile", "ephemeral");
        // key -> tenant:role, via host-scoped config (NOT a process-global env var,
        // so it can't leak into other test factories running in parallel).
        builder.UseSetting("ApiKeys", "key-alpha:alpha:admin,key-beta:beta:reader");
    }
}

public class SecurityIntegrationTests : IClassFixture<AuthApiFactory>
{
    private readonly AuthApiFactory _factory;
    public SecurityIntegrationTests(AuthApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_is_open_without_auth()
    {
        var resp = await _factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_credential_is_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer not-a-real-key");
        var resp = await client.GetAsync("/v1/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_is_scoped_to_the_keys_tenant_not_the_header()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer key-alpha");
        // Try to spoof a different tenant via header — must be ignored; key wins.
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "beta");

        // Ingest under the alpha key.
        var body = """
        { "gen_ai.provider.name":"anthropic", "gen_ai.response.model":"claude-opus-5",
          "gen_ai.usage.input_tokens":1000, "gen_ai.usage.output_tokens":500,
          "span_id":"sec-1", "kind":"llm" }
        """;
        var ingest = await client.PostAsync("/v1/ingest", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        // The span is visible under alpha...
        var underAlpha = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
        Assert.Equal(1, underAlpha.GetProperty("spanCount").GetInt32());

        // ...and NOT under beta (a different key), proving the header didn't leak it cross-tenant.
        var betaClient = _factory.CreateClient();
        betaClient.DefaultRequestHeaders.Add("Authorization", "Bearer key-beta");
        var underBeta = await betaClient.GetFromJsonAsync<JsonElement>("/v1/summary");
        Assert.Equal(0, underBeta.GetProperty("spanCount").GetInt32());
    }
}

/// <summary>Phase 7 / Increment 3 — review finding #2: billing connectors resolve a
/// TENANT-SCOPED secret so pooled SaaS can't bleed cross-tenant realized cost.</summary>
public class TenantScopedConnectorTests
{
    // Records which secret name was requested; returns a per-tenant key.
    private sealed class RecordingSecrets : ISecretProvider
    {
        public List<string> Requested { get; } = new();
        public Task<string?> GetAsync(string name, CancellationToken ct = default)
        {
            Requested.Add(name);
            return Task.FromResult<string?>($"key-for::{name}");
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? AuthHeader { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            AuthHeader = request.Headers.TryGetValues("Authorization", out var v) ? string.Join("", v) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task OpenAi_connector_resolves_a_tenant_scoped_secret_name()
    {
        var secrets = new RecordingSecrets();
        using var http = new HttpClient(new CapturingHandler()) { BaseAddress = new Uri("https://api.invalid") };
        var connector = new OpenAiBillingConnector(http, secrets);   // default template has {tenant}

        await connector.PullAsync("tenant-alpha", new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 5));

        var name = Assert.Single(secrets.Requested);
        Assert.Contains("tenant-alpha", name);        // per-tenant secret, not a global org key
        Assert.DoesNotContain("{tenant}", name);      // placeholder was substituted
    }
}

/// <summary>Phase 7 / Increment 3 — air-gap egress guard fails closed in solo/ephemeral.</summary>
public class EgressPolicyTests
{
    [Fact]
    public void Air_gap_profile_forbids_outbound_calls()
    {
        var solo = EgressPolicy.ForProfile("solo");
        Assert.True(solo.AirGapped);
        Assert.Throws<AirGapViolationException>(() => solo.AssertEgressAllowed("pricing.example.com", "live price sync"));
    }

    [Fact]
    public void Distributed_profile_allows_outbound_calls()
    {
        var dist = EgressPolicy.ForProfile("distributed");
        Assert.False(dist.AirGapped);
        dist.AssertEgressAllowed("pricing.example.com", "live price sync");   // no throw
    }
}
