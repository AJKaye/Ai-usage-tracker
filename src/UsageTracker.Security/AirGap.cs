namespace UsageTracker.Security;

/// <summary>
/// Air-gap egress policy (PROJECT_CONTEXT §6, D6/FedRAMP). In air-gap mode the
/// product must make NO outbound calls on any critical path — the offline pricing
/// bundle and local store are the only data sources. This is the fail-fast guard a
/// component consults before any outbound call: in air-gap mode it throws, so an
/// accidental egress (a live-sync catalog source, a billing connector) surfaces
/// immediately in test/CI rather than silently phoning home in a locked-down deploy.
/// </summary>
public sealed class EgressPolicy
{
    public bool AirGapped { get; }

    public EgressPolicy(bool airGapped) => AirGapped = airGapped;

    /// <summary>From the deployment profile: solo/ephemeral are air-gap-safe by default.</summary>
    public static EgressPolicy ForProfile(string profile) =>
        new(airGapped: profile is "solo" or "ephemeral");

    /// <summary>
    /// Assert an outbound call to <paramref name="host"/> is permitted. Throws
    /// <see cref="AirGapViolationException"/> in air-gap mode so the attempt fails
    /// closed. <paramref name="purpose"/> is included in the message for diagnosis.
    /// </summary>
    public void AssertEgressAllowed(string host, string purpose)
    {
        if (AirGapped)
            throw new AirGapViolationException(
                $"outbound call to '{host}' for '{purpose}' is forbidden in air-gap mode — " +
                "the solo/air-gap build must use the offline pricing bundle and local store only (D6/FedRAMP).");
    }
}

/// <summary>Thrown when a component attempts a network egress under an air-gap <see cref="EgressPolicy"/>.</summary>
public sealed class AirGapViolationException : Exception
{
    public AirGapViolationException(string message) : base(message) { }
}
