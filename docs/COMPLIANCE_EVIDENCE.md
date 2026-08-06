# Compliance Evidence Package

How to assemble an audit-ready evidence package for the four frameworks (SOC 2 Type II
· GDPR · HIPAA · FedRAMP). **This is a design-time evidence map, not a certification** —
certification requires an external audit against a running deployment.

## Sources of evidence (all first-party, in-product)

1. **Control register** — `GOVERNANCE.md` (also served live at `GET /v1/governance`
   and rendered on the in-product Regulatory Governance page). Maps each framework
   requirement → the module/control that satisfies it → honest status.
2. **Tamper-evident audit log** — `HashChainAuditSink.Export(tenant)`: an append-only,
   hash-chained record whose integrity is independently verifiable
   (`HashChainAuditSink.Verify`). This is the SOC 2 / HIPAA §164.312(b) audit evidence.
3. **Threat model** — `docs/THREAT_MODEL.md` (per-boundary STRIDE with mitigations +
   honest status).
4. **Test evidence** — the golden/contract suites (166 tests) demonstrate the control
   behaviors: tenant isolation (`EventStoreContractTests`, `SecurityIntegrationTests`),
   crypto-shred right-to-delete (`AuditAndShreddingTests`), air-gap egress
   (`EgressPolicyTests`), cost correctness (the §5 gotcha matrix).
5. **Supply chain** — CI builds `-warnaserror` incl. the NuGet vuln audit (caught a
   real CVE); release artifacts carry `SHA256SUMS` (+ optional detached signature).

## Assembling the package (per audit)

1. Export the control register (`GET /v1/governance`) → snapshot with date.
2. Export the audit chain per tenant (`HashChainAuditSink.Export`) + run `Verify` and
   record the result (proves non-tampering).
3. Attach `THREAT_MODEL.md`, the CI run (build + test + scan results), and the release
   `SHA256SUMS`(`.sig`).
4. Map each framework's controls using the tables in `GOVERNANCE.md`:
   - **SOC 2:** CC6.1/6.6/6.7 (access, boundary, at-rest), CC7.2 (monitoring/audit).
   - **GDPR:** Art. 17 (crypto-shred right-to-delete), 25 (content opt-in/excluded),
     32 (encryption/access), 30/33 (audit).
   - **HIPAA:** §164.312 (a) access, (b) audit, (c) integrity, (e) transmission.
   - **FedRAMP:** AC-4/SC-7 air-gap, SC-13 FIPS crypto, AU-* audit.

## Honest gaps to disclose to an auditor

Per `GOVERNANCE.md` + `THREAT_MODEL.md`, these are **Designed, not yet Implemented**
(infra-blocked in the current build): DB-enforced isolation (Postgres RLS / ClickHouse
row policies), transport security (mTLS/TLS 1.3), KMS-wrapped DEKs + FIPS mode, full
OIDC/SAML SSO + SCIM, and CI SBOM/secret-scan/image-signing. The app-layer equivalents
(principal-derived tenancy, per-subject AES-256-GCM keys, hash-chain audit, tenant-
scoped secrets) are Implemented/Verified. A third-party penetration test is a
prerequisite for certification and has not been performed.
