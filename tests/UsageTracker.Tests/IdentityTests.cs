using UsageTracker.Contracts;
using UsageTracker.Security;
using Xunit;

namespace UsageTracker.Tests;

/// <summary>
/// Phase 7 / Increment 1 — identity, tenant resolution from the authenticated
/// principal, and RBAC/ABAC (PROJECT_CONTEXT §6). Closes the review finding that
/// tenant came from an unauthenticated header: tenant now derives from the verified
/// Principal, and a cross-tenant assertion is provably rejected.
/// </summary>
public class IdentityTests
{
    private static ApiKeyIdentityProvider Provider() => new(new Dictionary<string, ApiKeyBinding>
    {
        ["key-alpha-admin"] = new() { TenantId = "alpha", SubjectId = "svc-a", Roles = new[] { "admin" }, Scopes = new[] { "scores:write" } },
        ["key-beta-reader"] = new() { TenantId = "beta", SubjectId = "svc-b", Roles = new[] { "reader" } },
    });

    private static Dictionary<string, string> Bearer(string key) => new() { ["Authorization"] = $"Bearer {key}" };

    [Fact]
    public async Task Valid_api_key_authenticates_to_its_bound_principal()
    {
        var p = await Provider().AuthenticateAsync(Bearer("key-alpha-admin"));
        Assert.NotNull(p);
        Assert.Equal("alpha", p!.TenantId);
        Assert.Equal("svc-a", p.SubjectId);
        Assert.Contains("admin", p.Roles);
    }

    [Fact]
    public async Task X_api_key_header_also_accepted()
    {
        var p = await Provider().AuthenticateAsync(new Dictionary<string, string> { ["X-Api-Key"] = "key-beta-reader" });
        Assert.Equal("beta", p!.TenantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-key")]
    public async Task Missing_or_unknown_credential_is_rejected(string key)
    {
        var headers = string.IsNullOrWhiteSpace(key) ? new Dictionary<string, string>() : Bearer(key);
        Assert.Null(await Provider().AuthenticateAsync(headers));
    }

    [Fact]
    public void Tenant_resolves_from_principal_not_a_header()
    {
        var resolver = new PrincipalTenantResolver();
        var principal = new Principal { SubjectId = "s", TenantId = "alpha" };
        // Even if the client sends a DIFFERENT tenant header, the principal's tenant wins.
        var headers = new Dictionary<string, string> { ["X-Tenant-Id"] = "beta" };
        Assert.Equal("alpha", resolver.Resolve(headers, principal));
    }

    [Fact]
    public void No_principal_cannot_resolve_a_tenant()
    {
        Assert.Throws<UnauthorizedAccessException>(
            () => new PrincipalTenantResolver().Resolve(new Dictionary<string, string>(), principal: null));
    }

    [Fact]
    public void Rbac_abac_enforce_tenant_match_role_and_scope()
    {
        var alphaAdmin = new Principal { SubjectId = "s", TenantId = "alpha", Roles = new[] { "admin" }, Scopes = new[] { "scores:write" } };
        var betaReader = new Principal { SubjectId = "s", TenantId = "beta", Roles = new[] { "reader" } };

        // ABAC: cross-tenant access is impossible.
        Assert.True(Authorizer.CanAccessTenant(alphaAdmin, "alpha"));
        Assert.False(Authorizer.CanAccessTenant(alphaAdmin, "beta"));

        // RBAC: admin is a superset; reader is not admin.
        Assert.True(Authorizer.HasRole(alphaAdmin, "reader"));   // admin covers reader
        Assert.False(Authorizer.HasRole(betaReader, "admin"));

        // Scope check.
        Assert.True(Authorizer.HasScope(alphaAdmin, "scores:write"));
        Assert.False(Authorizer.HasScope(betaReader, "scores:write"));
    }
}
