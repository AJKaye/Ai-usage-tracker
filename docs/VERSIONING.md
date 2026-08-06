# Versioning & Compatibility Policy

How contracts, plugins, and APIs evolve without breaking consumers. Grounded in the
`ContractVersion` gate that already ships (`UsageTracker.Contracts/Plugins.cs`).

## 1. Module contracts (`UsageTracker.Contracts`)

Semantic versioning on a single `ContractVersion { Major, Minor }`:

- **Minor bump (additive):** new interface, new optional member, new record, new
  optional field on an existing record. Backward-compatible — every prior consumer
  keeps working. This is how Phases 3–10 extended `Span`, `ModelRate`, `CostBreakdown`,
  `IPriceCatalog`, etc. (new members added as optional / default-interface-methods).
- **Major bump (breaking):** removing/renaming a member, changing a signature, changing
  semantics. Requires a migration note in the Decision Log and a plugin recompile.

**Rule:** breaking a contract = major bump + migration note. Additive = minor.

## 2. Plugins (adapters, pricing sources)

Each plugin declares the contract version it was built against:
`[assembly: UsageTrackerPlugin(contractMajor, contractMinor)]`. The host's
`PluginLoader` enforces at load time (tested in `PluginLoaderTests`):

- **Major mismatch → refused.** A plugin built against a different major won't load.
- **Plugin minor > host minor → refused.** The plugin needs additive features the host
  lacks.
- **Plugin minor ≤ host minor → loads.** Forward-compatible within a major.

So a plugin is safe to ship against a published contract version and will be *refused*,
never silently mis-run, against an incompatible host.

## 3. HTTP / serving API

- **Path-versioned:** all serving routes are under `/v1/…`. A breaking change ships a
  new prefix (`/v2/…`) and `/v1` is supported through a deprecation window.
- **Additive within a version:** new endpoints and new optional response fields are
  minor, non-breaking — clients ignore unknown fields.
- **MCP:** the `/mcp` face pins the MCP protocol version (currently `2025-06-18`) in
  the `initialize` response; a protocol change is advertised there.

## 4. Export bundle format

The data-portability `ExportBundle` carries an explicit `format` integer
(`ExportBundle.CurrentFormat`). Import rejects an unknown format rather than
mis-reading it — so a backup taken today is safe to restore into a compatible build,
and a future format change is detectable.

## 5. Deployment profiles

`solo` / `ephemeral` / `standard` / `distributed` select the backend by config, all
behind the same contracts. A profile is added without a contract change; a new backend
must pass the shared conformance suite (`EventStoreContractTests`) before a profile may
select it — that equivalence is what makes a `solo`→`distributed` migration a config +
data-export step, not a rewrite (see the migration runbook).

## 6. What "stable" means at GA

- The **`solo` single-file binary** + its `/v1` API + the export-bundle format are the
  stable public surface.
- The distributed/SaaS tiers are additive backends behind the same contracts.
- Any change that would break a `/v1` consumer, a published plugin, or an existing
  export bundle is a major event with a Decision Log entry and a migration path.
