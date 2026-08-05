using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UsageTracker.Security;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 8 / Increment 1 — the governance matrix parser (backs the Regulatory
/// Governance page, sourced from GOVERNANCE.md so it never drifts) + the API.
/// </summary>
public class GovernanceParserTests
{
    private const string Fixture = """
    # Regulatory Governance

    **Last updated:** 2026-08-05 · **Overall posture:** Through Phase 7.

    | ID | Control | Module / mechanism | Status | Evidence / notes |
    |----|---------|--------------------|--------|------------------|
    | C-IDENT | Authn via SSO → `Principal` | `IIdentityProvider` | **Verified (API-key)** | `ApiKeyIdentityProvider` tested |
    | C-AUDIT | Tamper-evident audit | `HashChainAuditSink` | **Implemented** | hash chain tested |
    | C-MTLS | Zero-trust mTLS | deployment mesh | Designed | Infra-blocked |
    """;

    [Fact]
    public void Parses_control_rows_status_and_last_updated()
    {
        var m = GovernanceParser.Parse(Fixture);

        Assert.Equal("2026-08-05", m.LastUpdated);
        Assert.Equal(3, m.Controls.Count);

        var ident = Assert.Single(m.Controls, c => c.Id == "C-IDENT");
        Assert.Equal("Verified (API-key)", ident.Status);           // bold stripped, qualifier kept
        Assert.DoesNotContain("*", ident.Status);
        Assert.DoesNotContain("`", ident.Mechanism);                // backticks stripped

        Assert.Equal("Designed", Assert.Single(m.Controls, c => c.Id == "C-MTLS").Status);
    }

    [Fact]
    public void Counts_statuses_for_the_page_summary()
    {
        var m = GovernanceParser.Parse(Fixture);
        Assert.Equal(1, m.StatusCounts["Verified"]);
        Assert.Equal(1, m.StatusCounts["Implemented"]);
        Assert.Equal(1, m.StatusCounts["Designed"]);
        Assert.Equal(0, m.StatusCounts["Certified"]);
    }

    [Fact]
    public void Non_control_rows_are_ignored()
    {
        // A framework-mapping table row (not "| C-…") must not be parsed as a control.
        const string md = """
        **Last updated:** 2026-08-05

        | ID | Control | Mechanism | Status | Evidence |
        |----|---------|-----------|--------|----------|
        | C-ONE | real control | mech | Implemented | ok |
        | CC6.1 | a framework row | refs | Designed | note |
        """;
        var m = GovernanceParser.Parse(md);
        Assert.Single(m.Controls);
        Assert.Equal("C-ONE", m.Controls[0].Id);
    }
}

/// <summary>Phase 8 / Increment 1 — governance API + static/SPA hosting end-to-end.</summary>
public class GovernanceApiTests : IClassFixture<EphemeralApiFactory>
{
    private readonly EphemeralApiFactory _factory;
    public GovernanceApiTests(EphemeralApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Governance_endpoint_is_public_and_returns_the_matrix()
    {
        var client = _factory.CreateClient();
        // No auth header — governance is a public compliance disclosure.
        var resp = await client.GetAsync("/v1/governance");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var matrix = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // The real GOVERNANCE.md (resolved from the repo root in test) has C- controls.
        Assert.True(matrix.GetProperty("controls").GetArrayLength() > 0, "expected controls parsed from GOVERNANCE.md");
        Assert.NotEqual("unavailable", matrix.GetProperty("lastUpdated").GetString());
    }
}
