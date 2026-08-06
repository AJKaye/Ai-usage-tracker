# Operator Runbooks

Operational procedures for the three deployment tiers. **Honest status:** the `solo`
(zero-infra) procedures are verified against the shipping binary. The distributed /
SaaS procedures are **authored against the designed topology but not yet exercised on
a live cluster** (no Docker/K8s available in the build environment) — the contracts +
data-portability seams that make them work are in place and tested; treat these as the
runbooks to *validate* when infra is provisioned.

Contents:
- [air-gap-install.md](./air-gap-install.md) — offline install (solo verified; cluster designed)
- [upgrade-and-migrate.md](./upgrade-and-migrate.md) — upgrades + **solo→distributed migration** (uses the export tooling)
- [backup-restore-dr.md](./backup-restore-dr.md) — backup/restore + disaster recovery
- [on-call-slo.md](./on-call-slo.md) — SLOs, the self-observability signal, alerting

See also `../QUICKSTART.md`, `../VERSIONING.md`, `../COMPLIANCE_EVIDENCE.md`.
