using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>Ephemeral API with opt-in content capture ENABLED (off by default).</summary>
public sealed class ContentEnabledApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Profile", "ephemeral");
        builder.UseSetting("ContentCapture", "true");
    }
}

/// <summary>
/// Security-wiring: the crypto-shred content path is now a real request-path feature.
/// Capture (opt-in) → reveal → erase-subject → content permanently unrecoverable,
/// while the cost aggregate (which never used the key) is unaffected. GDPR/HIPAA
/// right-to-delete over an append-only store.
/// </summary>
public class ContentShredTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Capture_reveal_then_crypto_shred_makes_content_unrecoverable()
    {
        var factory = new ContentEnabledApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "shred-t");

        // Ingest a costed span (the aggregate that must SURVIVE erasure).
        await client.PostAsync("/v1/ingest", Json(
            """{"gen_ai.provider.name":"anthropic","gen_ai.response.model":"claude-opus-5","gen_ai.usage.input_tokens":1000,"gen_ai.usage.output_tokens":500,"span_id":"shred-1","kind":"llm"}"""));

        // Capture opt-in content for that span, under subject "user-42".
        var cap = await client.PostAsync("/v1/content", Json(
            """{"span_id":"shred-1","subject_id":"user-42","content":"the user's private prompt"}"""));
        Assert.Equal(HttpStatusCode.Accepted, cap.StatusCode);

        // Reveal → decrypts.
        var reveal = await client.GetAsync("/v1/content/shred-1");
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
        using (var d = JsonDocument.Parse(await reveal.Content.ReadAsStringAsync()))
            Assert.Equal("the user's private prompt", d.RootElement.GetProperty("content").GetString());

        // Right-to-delete: crypto-shred the subject.
        var del = await client.DeleteAsync("/v1/subjects/user-42");
        using (var d = JsonDocument.Parse(await del.Content.ReadAsStringAsync()))
            Assert.True(d.RootElement.GetProperty("erased").GetBoolean());

        // Content is now permanently unrecoverable...
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/content/shred-1")).StatusCode);

        // ...but the cost aggregate persists (never depended on the key).
        var sum = await client.GetFromJsonAsync<JsonElement>("/v1/summary");
        Assert.Equal(1, sum.GetProperty("spanCount").GetInt32());
        Assert.Equal(0.0175m, sum.GetProperty("totalEstimatedCost").GetDecimal());
    }

    [Fact]
    public async Task Content_capture_is_off_by_default_returns_409()
    {
        // Default ephemeral factory does NOT enable capture.
        var factory = new EphemeralApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "no-capture");
        var resp = await client.PostAsync("/v1/content", Json(
            """{"span_id":"x","subject_id":"s","content":"nope"}"""));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);   // opt-in; off by default
    }
}
