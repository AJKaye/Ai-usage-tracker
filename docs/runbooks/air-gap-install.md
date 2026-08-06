# Air-Gap Install Runbook

## solo (zero-infra) — verified

The `solo` binary is air-gap-native: it makes **no outbound calls on any critical
path** (offline pricing bundle, local store). In `solo`/`ephemeral` an accidental
egress fails closed with `AirGapViolationException` (Phase 7 `EgressPolicy`).

1. On a networked box, download `usage-tracker` + `SHA256SUMS` (+ `.sig`).
2. Transfer to the air-gapped host by approved media; `sha256sum -c SHA256SUMS` and
   verify the signature against the release public key.
3. Run it. No network required — the cost engine prices from the embedded offline
   catalog seed.
4. **Signed pricing bundle (recommended for FedRAMP/D6):** provide a signed offline
   price bundle + its detached signature + the public key via
   `USAGETRACKER__PRICING_BUNDLE` / `__PRICING_BUNDLE_SIG` / `__PRICING_PUBKEY`. The
   bundle's ECDSA signature is verified before load; a tampered bundle is refused
   (verified: `CatalogSourceAndTier3Tests`, and enforced at the composition root).
5. Confirm zero egress: no critical-path component opens a socket outbound
   (`EgressPolicyTests`).

## distributed (air-gapped cluster) — designed (pending infra)

- Mirror container images + charts into the air-gapped registry (`deploy/` +
  Helm/K8s manifests — *authored/pending on a cluster*).
- Provide the signed offline pricing bundle as above; billing connectors stay
  **disabled** (air-gap: the estimate stands, `reconciledAgainstBilling=false` is
  surfaced, not silently zeroed — Phase 4).
- All telemetry stays in-cluster (dogfood OTel to an in-cluster collector).
- Validate with an egress-firewalled smoke test (the Phase-10 exit check) once the
  cluster exists.
