using System.Security.Cryptography;
using System.Text;
using UsageTracker.Contracts;

namespace UsageTracker.Security;

/// <summary>
/// A registered API key → the Principal it authenticates. In production the key
/// material lives in Vault/KMS (resolved via <see cref="ISecretProvider"/>); this
/// record is the resolved binding (tenant + roles + scopes) the key maps to.
/// </summary>
public sealed record ApiKeyBinding
{
    public required string TenantId { get; init; }
    public required string SubjectId { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();
    public string? WorkspaceId { get; init; }
}

/// <summary>
/// Authenticates a bearer / API-key credential into a <see cref="Principal"/>
/// (ARCHITECTURE.md §8.2; PROJECT_CONTEXT §6). Keys are matched by a constant-time
/// comparison of their SHA-256 so a timing side-channel can't probe them, and the
/// raw key is never stored — only its hash. This is the OIDC/SAML seam: swap this
/// impl for a JWT/SAML validator without touching callers.
/// </summary>
public sealed class ApiKeyIdentityProvider : IIdentityProvider
{
    // sha256(key) -> binding. Storing the HASH, never the key.
    private readonly IReadOnlyDictionary<string, ApiKeyBinding> _byKeyHash;

    public ApiKeyIdentityProvider(IReadOnlyDictionary<string, ApiKeyBinding> byRawKey)
        => _byKeyHash = byRawKey.ToDictionary(kv => Hash(kv.Key), kv => kv.Value);

    public Task<Principal?> AuthenticateAsync(IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        var raw = ExtractCredential(headers);
        if (string.IsNullOrWhiteSpace(raw)) return Task.FromResult<Principal?>(null);

        var presentedHash = Hash(raw);
        // Constant-time scan: compare against every known hash so timing doesn't leak which prefix matched.
        ApiKeyBinding? match = null;
        foreach (var (hash, binding) in _byKeyHash)
            if (FixedTimeEquals(hash, presentedHash))
                match = binding;

        if (match is null) return Task.FromResult<Principal?>(null);
        return Task.FromResult<Principal?>(new Principal
        {
            SubjectId = match.SubjectId,
            TenantId = match.TenantId,
            Roles = match.Roles,
            Scopes = match.Scopes,
            WorkspaceId = match.WorkspaceId,
        });
    }

    public static string? ExtractCredential(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Authorization", out var auth) && !string.IsNullOrWhiteSpace(auth))
            return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : auth.Trim();
        if (headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            return apiKey.Trim();
        return null;
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}

/// <summary>
/// Resolves the tenant a request operates in FROM the authenticated principal — the
/// single chokepoint every read/write is scoped by (D5). Crucially, the tenant comes
/// from the verified <see cref="Principal.TenantId"/>, NOT a client-supplied header,
/// so a caller cannot assert an arbitrary tenant (closes the review's header-spoofing
/// finding). A header may only REQUEST a workspace within the principal's tenant.
/// </summary>
public sealed class PrincipalTenantResolver : ITenantResolver
{
    public string Resolve(IReadOnlyDictionary<string, string> headers, Principal? principal)
    {
        if (principal is null)
            throw new UnauthorizedAccessException("no authenticated principal — cannot resolve tenant.");
        return principal.TenantId;
    }
}

/// <summary>
/// RBAC + ABAC authorization (PROJECT_CONTEXT §6). Checks a principal holds a
/// required role/scope AND that the resource it targets is within the principal's
/// tenant (attribute-based: tenant-match is non-negotiable). Pure + deterministic
/// so it's trivially testable and usable as middleware.
/// </summary>
public static class Authorizer
{
    /// <summary>ABAC: the principal may only act within its own tenant.</summary>
    public static bool CanAccessTenant(Principal principal, string targetTenantId) =>
        string.Equals(principal.TenantId, targetTenantId, StringComparison.Ordinal);

    /// <summary>RBAC: principal holds the role (case-insensitive), or the "admin" superset.</summary>
    public static bool HasRole(Principal principal, string role) =>
        principal.Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

    /// <summary>Scope check for fine-grained API permissions (e.g. "scores:write").</summary>
    public static bool HasScope(Principal principal, string scope) =>
        principal.Scopes.Any(s => string.Equals(s, scope, StringComparison.OrdinalIgnoreCase));
}
