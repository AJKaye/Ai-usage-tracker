# Regulatory Governance — Control → Requirement Matrix

> **The backing data for the in-product Regulatory Governance page (D6).** It maps each compliance framework's requirements to the specific design element / module / control that satisfies them, with an honest implementation status. The Phase 8 UI renders this (from `GOVERNANCE.md` or an API derived from it) so the page never drifts from reality. Companion to [`PROJECT_CONTEXT.md`](./PROJECT_CONTEXT.md) §6.
>
> **This is a design-time control register, not a compliance attestation.** No framework is certified; certification requires an audit against a running system. Status legend below is deliberately conservative.

**Last updated:** 2026-08-05 · **Overall posture:** Through Phase 7. The **app-layer** security controls (identity, RBAC/ABAC, tenant isolation, tamper-evident audit, crypto-shredding, secrets-by-name, air-gap egress) are Implemented/Verified with tests. **DB-enforced isolation (Postgres RLS / ClickHouse row policies), transport security (mTLS/TLS 1.3), KMS/FIPS, and CI supply-chain scans remain infra-blocked** and are Designed. Do not represent any row as "certified."

## Status legend

| Status | Meaning |
|--------|---------|
| **Designed** | Control is specified in the architecture/contracts; not yet implemented |
| **Scaffolded** | A contract/seam exists in code (e.g. `IAuditSink`), no production implementation |
| **Implemented** | Built and tested in the codebase |
| **Verified** | Implemented **and** covered by an automated test/gate |
| **Certified** | Passed an external audit — *none yet, by definition at this phase* |

---

## Cross-framework foundational controls

These controls satisfy requirements shared across SOC 2 / GDPR / HIPAA / FedRAMP. Framework-specific rows below reference them.

| ID | Control | Module / mechanism (§5) | Status | Evidence / notes |
|----|---------|--------------------------|--------|------------------|
| C-IDENT | Authn via OIDC/SAML SSO → `Principal` | `IIdentityProvider` | **Verified (API-key)** | `ApiKeyIdentityProvider` (hashed keys, constant-time match) + auth middleware; tested + live-verified on the exe. OIDC/SAML validator swaps in behind the same contract |
| C-AUTHZ | RBAC + ABAC, tenant/workspace/role scoped | `Authorizer` + middleware | **Implemented** | `Authorizer` (roles/admin-superset, tenant-match, scopes) built + tested; per-endpoint filter is remaining wiring |
| C-TENANT | Tenant isolation on every read/write | `PrincipalTenantResolver` + Postgres RLS / CH row policies | **Verified (app-layer)** | Tenant from verified principal (not header); stores contract-tested for no cross-tenant read; auth E2E proves header-spoof rejected. **DB-enforced RLS/row-policies infra-blocked** |
| C-MTLS | Zero-trust service-to-service mTLS | deployment mesh | Designed | Infra-blocked (needs a cluster) |
| C-ENC-TRANSIT | TLS 1.3 in transit | ingress / mesh | Designed | Deployment concern (Phase 10) |
| C-ENC-REST | AES-256 at rest; envelope encryption via KMS/Vault | `SubjectKeyVault` + storage | **Implemented (content)** | Per-subject AES-256-GCM sealing built + tested; disk-level at-rest + KMS-wrapped DEK is infra |
| C-SECRETS | No secrets in config/code/images — resolved by name | `ISecretProvider` | **Verified** | Connectors resolve keys by tenant-scoped name, throw if missing; no hardcoded secrets (audited Phase 0–5); `.gitignore` excludes `.env` |
| C-AUDIT | Immutable, tamper-evident audit of access + admin actions | `HashChainAuditSink` | **Implemented** | Hash-chain sink built + tested (detects mutation/reorder), exportable evidence. Emission on **every** path is remaining wiring |
| C-SUPPLY | Supply-chain: SBOM, dependency + secret scanning, signed images | CI (`.github/workflows/ci.yml`) | Scaffolded | `-warnaserror` + NuGet vuln audit present (caught a real CVE); SBOM/scan/sign still to add |
| C-RETAIN | Configurable retention per tenant/region | `DataLifecyclePolicy` | **Implemented (policy)** | Retention window + `IsExpired` built + tested; purge job over a server store is infra |
| C-RESIDENCY | Data residency per tenant/region | `DataLifecyclePolicy.AllowsRegion` | **Implemented (policy)** | Residency guard built + tested; storage routing is deployment topology |
| C-DELETE | Right-to-delete over append-only store via crypto-shredding | `SubjectKeyVault` per-subject data keys | **Verified** | `CryptoShred` destroys the key → content unrecoverable while aggregates persist; tested |
| C-CONTENT | Prompt/response capture is opt-in + field-level encrypted + PII-classified | canonical model (content held separately) | **Implemented (slice)** | Canonical `Span` deliberately excludes content; capture is a separate opt-in path |

---

## SOC 2 Type II

| Trust Services Criterion | Requirement | Satisfied by | Status |
|--------------------------|-------------|--------------|--------|
| CC6.1 Logical access | Restrict access to authorized users | C-IDENT, C-AUTHZ, C-TENANT | Designed / slice-verified for tenant scope |
| CC6.6 Boundary protection | Encrypt data in transit; zero-trust | C-ENC-TRANSIT, C-MTLS | Designed |
| CC6.7 Data at rest | Encrypt stored data | C-ENC-REST | Designed |
| CC7.2 Monitoring | Detect + log security-relevant events | C-AUDIT + platform self-observability (OTel) | Scaffolded |
| CC8.1 Change management | Controlled, reviewed, tested changes | CI gates + ADRs (`docs/adr/`) + contract-version gate | **Verified (slice)** — CI build+test; ADRs recorded; plugin version gate tested |
| CC3.2 / A1 Availability | Resilience, no data loss | Kafka at-least-once + idempotent dedup | Designed (dedup verified in slice) |

## GDPR / data residency

| Article / topic | Requirement | Satisfied by | Status |
|-----------------|-------------|--------------|--------|
| Art. 17 Right to erasure | Delete a subject's personal data on request | C-DELETE (crypto-shredding) | Designed |
| Art. 25 Data protection by design | Minimize + protect PII by default | C-CONTENT (capture opt-in, content excluded from canonical model) | Implemented (slice) |
| Art. 32 Security of processing | Encryption, access control, resilience | C-ENC-REST, C-ENC-TRANSIT, C-AUTHZ | Designed |
| Ch. V / residency | Keep data in a chosen region | C-RESIDENCY | Designed |
| Art. 30 Records of processing | Auditable processing log | C-AUDIT | Scaffolded |
| Art. 33 Breach notification | Detect + record access | C-AUDIT + monitoring | Designed |

## HIPAA

| Safeguard | Requirement | Satisfied by | Status |
|-----------|-------------|--------------|--------|
| §164.312(a) Access control | Unique user id, least privilege | C-IDENT, C-AUTHZ | Designed |
| §164.312(b) Audit controls | Record + examine activity on ePHI | C-AUDIT | Scaffolded |
| §164.312(c) Integrity | Protect ePHI from improper alteration | C-AUDIT (tamper-evident) + C-ENC-REST | Designed |
| §164.312(e) Transmission security | Encrypt ePHI in transit | C-ENC-TRANSIT | Designed |
| §164.308 Administrative | BAA-ready PHI handling; retention | C-RETAIN, C-CONTENT | Designed |
| §164.312(a)(2)(iv) Encryption | Encrypt ePHI at rest | C-ENC-REST | Designed |

## FedRAMP / air-gap

| Control family | Requirement | Satisfied by | Status |
|----------------|-------------|--------------|--------|
| SC-13 Cryptographic protection | FIPS-validated crypto | C-ENC-REST/TRANSIT with FIPS mode toggle | Designed |
| AC-4 Information flow / air-gap | No outbound calls on critical paths | Offline pricing bundle (`IPriceCatalogSource` offline impl); connectors optional | **Implemented (slice)** — cost engine runs fully offline on the seed bundle; no network on the cost path |
| SC-7 Boundary protection | Fully offline operation | Air-gap deployment mode (D3) | Designed |
| AU-* Audit | Comprehensive, protected audit | C-AUDIT | Scaffolded |
| CM-* Configuration mgmt | Controlled config, versioned | ADRs + IaC (`deploy/`) + CI | Designed / slice-verified (ADRs, CI) |
| SA-* Supply chain | SBOM, provenance, signing | C-SUPPLY | Designed |

---

## How this file stays truthful

1. **Every phase updates the status column** for controls it touches (the DEVELOPMENT_PLAN Phase 7 exit criteria require each row to reach at least Implemented/Verified).
2. **The Regulatory Governance page reads this matrix** (or an API projection of it) — never a hand-maintained copy — so the product's compliance claims equal the code's actual state.
3. **No row claims "Certified"** until an external audit says so. Overstating status here is a compliance risk in itself.
