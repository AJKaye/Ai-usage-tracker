namespace UsageTracker.Contracts;

/// <summary>
/// Authenticated caller identity (PROJECT_CONTEXT.md §6). Produced by
/// <see cref="IIdentityProvider"/> from a token; carries the tenant + roles that
/// authZ and tenancy scoping key on.
/// </summary>
public sealed record Principal
{
    public required string SubjectId { get; init; }
    public required string TenantId { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public string? WorkspaceId { get; init; }
}

/// <summary>
/// Validates an inbound credential (OIDC/SAML bearer, mTLS cert, API key) into a
/// <see cref="Principal"/>. Swappable so SSO provider is config, not code.
/// </summary>
public interface IIdentityProvider
{
    Task<Principal?> AuthenticateAsync(IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}

/// <summary>
/// Resolves the tenant for a request — the single chokepoint every read/write is
/// scoped by (D5 shared-schema + RLS). In the slice this reads a header; in
/// production it derives from the authenticated <see cref="Principal"/>.
/// </summary>
public interface ITenantResolver
{
    string Resolve(IReadOnlyDictionary<string, string> headers, Principal? principal);
}

/// <summary>
/// Immutable, tamper-evident audit sink for all access + admin actions
/// (PROJECT_CONTEXT.md §6; SOC 2 evidence). Append-only; tenant-scoped.
/// </summary>
public interface IAuditSink
{
    Task RecordAsync(AuditEvent evt, CancellationToken ct = default);
}

public sealed record AuditEvent
{
    public required string TenantId { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }     // e.g. "span.read", "vault.credential.create"
    public string? TargetId { get; init; }
    public required DateTimeOffset At { get; init; }
    public IReadOnlyDictionary<string, string>? Detail { get; init; }
    public bool Success { get; init; } = true;
}

/// <summary>
/// Resolves a secret by name (PROJECT_CONTEXT.md §6: secrets never in
/// config/code/images — referenced by name, resolved from Vault/KMS/cloud secret
/// manager). Air-gap deployments back this with a local sealed store.
/// </summary>
public interface ISecretProvider
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);
}
