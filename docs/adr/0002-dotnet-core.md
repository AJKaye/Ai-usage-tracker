# ADR 0002 — .NET 10 / C# for the core (D2)

**Status:** Accepted (2026-08-04) · **Decider:** Product owner

## Context
The ingestion/cost/reconciliation services need high throughput, strong enterprise + security tooling, and single-artifact deploys. Candidates: Go, TypeScript/Node, Python, .NET/C#.

## Decision
**.NET 10 (LTS), C#** for all backend services (ASP.NET Core). Frontend is a separate stack (ADR-0004).

## Consequences
- (+) High throughput, mature DI/hosting, FIPS-capable crypto, strong enterprise/security ecosystem, single-file publish.
- (+) `AssemblyLoadContext` gives a clean plugin isolation model for the adapter seam.
- (−) A new language for the owner's usual PowerShell/JS/Python toolchain; mitigated by the module contracts keeping surface area small.
- Verified locally on **.NET SDK 10.0.302** (installed to `~/.dotnet`). Solution uses the `.slnx` format (.NET 10 default).
