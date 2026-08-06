# Threat Model v1 (STRIDE) — AI Usage Tracker

> Per-module STRIDE analysis backing the D6 posture (PROJECT_CONTEXT §6). Status
> reflects code as of Phase 7. "✅ mitigated" = implemented + tested here;
> "◑ partial" = seam/embedded impl present, scale-tier hardening pending;
> "○ planned" = designed, not yet built (usually infra-blocked). Companion:
> `GOVERNANCE.md` (control→framework matrix).

## Trust boundaries

1. **Client → Ingestion API** — untrusted callers over HTTP. Auth + tenant scoping enforced here.
2. **API → Event/Reconciliation/Score stores** — in-process (solo) or network (scale tier).
3. **API → external provider billing/pricing APIs** — outbound; forbidden on air-gap critical paths.
4. **Plugin → host** — adapters load into the host process via `AssemblyLoadContext`.
5. **Operator → audit log** — even privileged operators must not silently alter history.

## STRIDE by boundary

| # | Threat | Vector | Mitigation | Status |
|---|--------|--------|-----------|--------|
| **S**poofing | Caller asserts another tenant | `X-Tenant-Id` header spoof | Tenant derives from the verified `Principal.TenantId`, never a header (`PrincipalTenantResolver`); presented-but-invalid credential → 401 | ✅ mitigated (`SecurityIntegrationTests`) |
| **S**poofing | Forged API key | Guess/replay a key | Keys stored as SHA-256 only; constant-time compare (`CryptographicOperations.FixedTimeEquals`) defeats timing probes | ✅ mitigated |
| **S**poofing | Malicious plugin impersonates core | Drop-in DLL | Plugin declares a contract version; host refuses on major-mismatch / too-new-minor (`PluginLoader`) | ✅ mitigated (`PluginLoaderTests`) |
| **T**ampering | Alter/reorder audit history | Operator edits the log | Hash-chain audit (`HashChainAuditSink`): each entry hashes the prior; `Verify` detects mutation/insertion/deletion/reorder | ✅ mitigated (`AuditAndShreddingTests`) |
| **T**ampering | Swap the pricing catalog | Poisoned offline bundle | ECDSA-P256/SHA-256 detached signature verified before load; tampered bundle throws (`EcdsaBundleVerifier`, `SignedOfflineBundleSource`); wired via `USAGETRACKER__PRICING_*` | ✅ mitigated |
| **T**ampering | Mutate opt-in content at rest | Store-level edit | AES-256-GCM sealing — an auth-tag mismatch on unseal returns null, never silent garbage | ✅ mitigated |
| **R**epudiation | "I didn't run that admin action" | Deny access/change | Tenant-scoped, append-only, tamper-evident audit trail; exportable as evidence | ◑ partial (sink built + tested; not yet emitted on *every* admin path) |
| **I**nformation disclosure | Cross-tenant data read | Query another tenant's spans | Every store call is tenant-scoped; contract-conformance suite asserts no cross-tenant read on both stores | ✅ mitigated (`EventStoreContractTests`) |
| **I**nformation disclosure | Realized-cost bleed in pooled SaaS | Global billing key | Billing connectors resolve a **tenant-scoped** secret name (`{tenant}` template) | ✅ mitigated (`TenantScopedConnectorTests`) |
| **I**nformation disclosure | Prompt/response content leak | Content persisted | Content is NOT on the canonical span; `EstimationText` is `[JsonIgnore]` (never serialized); capture is opt-in, off by default | ✅ mitigated |
| **I**nformation disclosure | Secret in logs/config/image | Hardcoded key | Secrets referenced by name via `ISecretProvider`, resolved from Vault/KMS; none in code/config (audited Phase 0–5) | ✅ mitigated |
| **D**enial of service | Ingestion flood | Unbounded intake | Bounded async channel (backpressure, no unbounded buffer); oversized OTLP body → 413 | ◑ partial (backpressure done; rate-limiting per-tenant pending) |
| **E**levation of privilege | Reader performs admin action | Missing authЗ check | RBAC (`HasRole`, admin superset) + ABAC (`CanAccessTenant`) + scopes (`HasScope`) | ◑ partial (primitives built + tested; per-endpoint enforcement to be applied broadly) |
| **E**levation | Egress from an air-gapped node | Data exfil / phone-home | `EgressPolicy` fails closed in solo/ephemeral; a critical-path outbound call throws `AirGapViolationException` | ✅ mitigated (`EgressPolicyTests`) |

## Infra-blocked mitigations (scale tier — designed, not built here)

- **Postgres RLS + ClickHouse row policies** — DB-enforced tenancy on top of the app-level scoping (needs Docker).
- **mTLS between services / TLS 1.3 termination** — zero-trust transport (needs a cluster).
- **KMS/Vault-wrapped data keys (envelope encryption) + FIPS mode** — the `SubjectKeyVault` is the embedded core; production wraps the DEK with a KMS KEK.
- **Static/dynamic security scans, SBOM, image signing, dependency+secret scanning in CI** — Phase 0 remainder; only `-warnaserror`+NuGet-audit present today.
- **Third-party penetration test** — Phase 10.

## Residual risks to accept or close before GA

1. ~~Audit not emitted on every path~~ — **CLOSED (2026-08-06):** middleware records a tamper-evident `AuditEvent` on every mutating `/v1`+`/mcp` request; `GET /v1/audit` exports + verifies. (Live-verified.)
2. Per-endpoint RBAC/scope enforcement is not yet applied to all routes — close by an authorization filter keyed on the resolved `Principal`. *(Still open.)*
3. Per-tenant rate limiting is not implemented (DoS) — close with a limiter middleware. *(Still open.)*
4. All DB-enforced isolation + transport security is infra-blocked here and verified only at the app layer — close on a Docker/cluster host.
5. ~~Air-gap egress guard decorative~~ — **CLOSED (2026-08-06):** `IEgressGuard` enforced at `HttpCatalogSource` + billing-connector call sites (fails closed under `solo`; handler provably not reached).
6. ~~Crypto-shred/audit proven as libraries only~~ — **CLOSED (2026-08-06):** crypto-shred is a live path (`POST /v1/content` opt-in → `DELETE /v1/subjects/{id}` → content unrecoverable, aggregates persist).
