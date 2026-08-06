using System.Net;
using System.Text;
using UsageTracker.Cost;
using UsageTracker.Reconciliation;
using UsageTracker.Security;
using UsageTracker.Contracts;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Security-wiring: air-gap now fails CLOSED at the actual outbound call sites
/// (HttpCatalogSource, billing connectors), not merely by not registering them.
/// An air-gapped EgressGuard makes the call throw BEFORE any HTTP is attempted.
/// </summary>
public class EgressEnforcementTests
{
    // A handler that fails the test if it is ever reached — proves the guard short-circuits.
    private sealed class TripwireHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>("k");
    }

    private static HttpClient Client(HttpMessageHandler h) => new(h) { BaseAddress = new Uri("https://api.invalid") };

    [Fact]
    public void HttpCatalogSource_fails_closed_when_air_gapped()
    {
        var tripwire = new TripwireHandler();
        var src = new HttpCatalogSource(Client(tripwire), new Uri("https://pricing.invalid/m.json"), "v",
            egress: EgressPolicy.ForProfile("solo"));   // air-gapped
        Assert.Throws<AirGapViolationException>(() => src.Load());
        Assert.False(tripwire.WasCalled, "outbound call must not be attempted under air-gap");
    }

    [Fact]
    public void HttpCatalogSource_allowed_when_not_air_gapped()
    {
        var body = """{ "gpt-5.6": { "input_cost_per_token": 0.000005, "output_cost_per_token": 0.000015 } }""";
        // Not air-gapped → the guard permits the call and it proceeds to the handler.
        var src = new HttpCatalogSource(
            new HttpClient(new CannedHandler(body)) { BaseAddress = new Uri("https://api.invalid") },
            new Uri("https://pricing.invalid/m.json"), "v", egress: EgressPolicy.ForProfile("distributed"));
        Assert.NotEmpty(src.Load());
    }

    [Fact]
    public async Task Billing_connectors_fail_closed_when_air_gapped()
    {
        var tOpenAi = new TripwireHandler();
        var openai = new OpenAiBillingConnector(Client(tOpenAi), new FakeSecrets(),
            egress: EgressPolicy.ForProfile("solo"));
        await Assert.ThrowsAsync<AirGapViolationException>(
            () => openai.PullAsync("t", new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 6)));
        Assert.False(tOpenAi.WasCalled);

        var tAnthropic = new TripwireHandler();
        var anthropic = new AnthropicBillingConnector(Client(tAnthropic), new FakeSecrets(),
            egress: EgressPolicy.ForProfile("solo"));
        await Assert.ThrowsAsync<AirGapViolationException>(
            () => anthropic.PullAsync("t", new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 6)));
        Assert.False(tAnthropic.WasCalled);
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
