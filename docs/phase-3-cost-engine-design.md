# Phase 3 — Cost Engine & Price Catalog: Design Spec

> **Status:** design locked 2026-08-05. Implementation in verified increments on branch
> `phase-3-cost-engine`. This is the acceptance spec for Phase 3 (the §5 gotcha matrix at
> the end is the definition of done). Companion: `ARCHITECTURE.md` §4/§5 (the *why*),
> `DEVELOPMENT_PLAN.md` Phase 3 (the deliverable checklist), `PROJECT_CONTEXT.md` §5 (contracts).

**Goal (from the plan):** turn normalized usage into *estimated cost* via the 3-tier engine over
a modular, versioned, composite-key catalog — and handle every §5 cost gotcha, each with a
golden test priced against hand-computed expected values.

**Hard constraints (obeyed throughout):**
1. **Non-breaking.** All contract changes are additive optional fields / new records / new
   interfaces. Every existing test in `CostEngineGoldenTests.cs` (and the whole 43-test suite)
   passes unchanged. `IPriceCatalog.Resolve(Span)` and `IPriceCatalogSource.Load()` signatures
   are preserved (new members are C# **default interface methods**).
2. **Fit the existing seams.** `ICostTier` (`int Order` + `CostBreakdown? TryCost(Span)`), tried
   ascending, **first non-null wins**. Extend the chain; don't redesign it.
3. **Money is `decimal`** everywhere. Per-token USD unless a `PricingMode` says otherwise. Never
   introduce binary float into money (`JsonElement.GetDecimal()` parses literal text).
4. **Offline-verifiable.** No test makes a real network call. The live-sync source takes an
   injected `HttpMessageHandler`; tests feed a canned response.
5. **Snapshot the rate per event** so historical recompute reproduces the original cost exactly.
6. **Progressive Deployment / air-gap.** Everything works in the zero-infra `solo` profile; the
   offline pricing bundle is signature-verifiable offline (D6/FedRAMP).

---

## 1. Locked cross-cutting decisions

**LD-1 — Dimension attachment: typed-first hybrid.** Pricing dimensions attach as **optional
nullable typed fields on `Span`** (consistent with how `Granularity`/`UnitsConsumed`/`UnitType`
already sit), with `Span.Metadata` as the escape hatch for rare/tenant-specific dims. New Span
fields (all optional → non-breaking): `ServiceTier`, `IsBatch`, `Region`, `DeploymentType`,
`ToolCalls`, `TokenizerId`. Rationale: typed fields are testable and let the catalog resolver read
selectors directly; a Metadata-only design stringly-types everything and makes resolution fragile.
Context-tier is **derived** from `Usage.InputTokens` (no field needed).

**LD-2 — `ModelRate` vs `UnitRate`: separate records.** Token rates (`ModelRate`, 5 buckets) and
coarse per-unit rates (`UnitRate`, single price) key differently (model vs unit-type) and share
almost no fields; merging leaves most fields meaningless on each row. `ModelRate` gains only a few
optional fields (`Mode`, `Multiplier`, `LongContextThresholdTokens`, modality/hourly rates,
`SourceId`, `EffectiveFrom`). `UnitRate` is new.

**LD-3 — Additive costs (tool surcharges, multipliers) compute *inside* the winning tier, not as
separate chain tiers.** The tier chain is **first-non-null-wins**, so a tier cannot "add on top" of
another. Tool surcharges (#7) and geo/residency multipliers (#9) are therefore applied *within*
`PriceMapCostTier` (which already resolves the model rate), emitted as extra `CostComponent`s.
This is the single most important structural note — it's why we don't add a "surcharge tier."

**LD-4 — Snapshot the whole resolved `ModelRate`.** `RateSnapshot.Rate` holds the entire resolved
rate object (not scalar copies). Consequence: every dimension another gotcha adds to `ModelRate`
(mode, context-tier, multiplier, modality) is snapshotted and recomputed *for free*. Any cost
computed from data **not** on the resolved `ModelRate` (e.g. tool-call counts) must also be
recorded so recompute doesn't drift — tool surcharges are reproduced from `Span.ToolCalls` +
snapshot, which are both persisted.

**LD-5 — OTLP coarse/extension attributes use the `aiusage.*` namespace** (gen_ai.* has no unit/
credit/tokenizer/tier keys), pinned in `SemConv.cs` behind the mapper. Defaults preserve current
OTLP behavior (Granularity→Token, all new fields null) so every Phase-2 golden passes unchanged.

---

## 2. Consolidated contract delta (all ADDITIVE)

### `Contracts/CanonicalModel.cs`

```csharp
public enum PricingMode { PerToken, PerUnit, PerRequest, PerSeat, PerHour }  // NEW

public enum ContextTier { Any, Standard, Long }                              // NEW

public sealed record ToolCall(string ToolType, int Count);                   // NEW  (#7)

// NEW — the per-event rate snapshot (§4.1 "store the rate, not the cost"; #14, #10)
public sealed record RateSnapshot
{
    public required ModelRate Rate { get; init; }         // whole resolved rate — source of truth for replay
    public required string CatalogSourceId { get; init; } // "offline-bundle" | "litellm" | "live-sync.google" …
    public required string CatalogVersion { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public DateOnly? EffectiveFrom { get; init; }
    public IReadOnlyDictionary<string,string>? RateKey { get; init; } // matched dims (service_tier/region/…)
    public string? TokenizerId { get; init; }             // #10 tokenizer drift attribution
    public string? CatalogDigest { get; init; }           // SHA-256 of the signed bundle (D6), when known
}

public sealed record CostBreakdown            // + one optional field
{
    /* existing: TotalCost, Currency, Components, Tier, CatalogVersion */
    public RateSnapshot? RateSnapshot { get; init; }      // NEW — null for IngestedUsd/Unpriced
}

public sealed record Span                      // + optional pricing selectors (all nullable)
{
    /* existing fields unchanged */
    public string? ServiceTier { get; init; }             // "standard"|"priority"|"flex"|"scale"…
    public bool? IsBatch { get; init; }                   // #4 batch discount
    public string? Region { get; init; }                  // #9 / #11
    public string? DeploymentType { get; init; }          // "global-standard"|"data-zone"|"ptu"…
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; } // #7
    public string? TokenizerId { get; init; }             // #10
}
```

### `Contracts/Interfaces.cs`

```csharp
public sealed record ModelRate                 // + optional fields (default = current behavior)
{
    /* existing: Model, Currency, Input/Output/CacheRead/CacheCreation/Reasoning PerToken, CatalogVersion */
    public PricingMode Mode { get; init; } = PricingMode.PerToken;
    public decimal Multiplier { get; init; } = 1.0m;      // #9 geo/residency; applied to token total
    public long? LongContextThresholdTokens { get; init; } // #5 whole-request re-rate threshold
    public decimal? AudioPerToken { get; init; }          // #6 modality
    public decimal? ImagePerToken { get; init; }          // #6 modality
    public decimal? HourlyRate { get; init; }             // #8 PerHour (PTU/provisioned)
    public string SourceId { get; init; } = "";           // provenance for the snapshot
    public DateOnly? EffectiveFrom { get; init; }         // #14 date-effective
    public DateOnly? EffectiveTo { get; init; }
    public ContextTier ContextTier { get; init; } = ContextTier.Any; // which tier this variant is
    public bool IsBatch { get; init; }                    // which batch state this variant is
    public string? ServiceTier { get; init; }             // which service tier this variant is
    public string? Region { get; init; }
    public string? DeploymentType { get; init; }
}

public sealed record UnitRate                  // NEW (LD-2)
{
    public required string UnitType { get; init; }        // "ai_unit"|"premium_request"|"copilot_seat"…
    public required PricingMode Mode { get; init; }       // PerUnit|PerRequest|PerSeat
    public required decimal PricePerUnit { get; init; }
    public string Currency { get; init; } = "USD";
    public string? Provider { get; init; }                // optional scope
    public string? Model { get; init; }                   // optional scope (per-model multiplier)
    public required string CatalogVersion { get; init; }
}

public interface IPriceCatalog                 // + default methods (non-breaking)
{
    ModelRate? Resolve(Span span);
    string Version { get; }
    UnitRate? ResolveUnit(Span span) => null;             // NEW
    decimal? ToolSurcharge(string toolType) => null;      // NEW (#7) per-call USD
}

public interface IPriceCatalogSource           // + default methods (non-breaking)
{
    IReadOnlyList<ModelRate> Load();
    string SourceId { get; }
    IReadOnlyList<UnitRate> LoadUnits() => Array.Empty<UnitRate>();     // NEW
    IReadOnlyDictionary<string,decimal> LoadToolSurcharges() => new Dictionary<string,decimal>(); // NEW
}

public interface ICostRecomputer               // NEW (#14 verification)
{
    CostBreakdown? RecomputeFromSnapshot(Span span);      // stable across catalog changes
    CostBreakdown? RecomputeFromLiveCatalog(Span span);   // "what it costs today"
}

public interface ITokenizer                    // NEW (Tier-3)
{
    string Id { get; }
    long CountTokens(string text);
}

public interface IBundleVerifier               // NEW (D6 air-gap)
{
    // Throws if signature invalid; returns the bundle's SHA-256 digest on success.
    string VerifyAndDigest(byte[] bundleBytes, byte[] signature);
}
```

---

## 3. The tier chain (final)

| Order | Tier | Fires when | Produces |
|---|---|---|---|
| 10 | `IngestedUsdCostTier` *(exists)* | span already carries an `IngestedUsd` cost | that cost verbatim |
| 20 | `PriceMapCostTier` *(extended)* | `Usage != null` **and** a `ModelRate` resolves | token buckets + **modality** (#6) + **PerHour** branch (#8) + **tool surcharges** (#7) + **×Multiplier** (#9); **records `RateSnapshot`** (#14/#10) |
| 30 | `CoarseUnitCostTier` *(new, SA4)* | `Granularity != Token` **and** a `UnitRate` resolves | `UnitsConsumed × PricePerUnit` (#15, #8 per_unit/request/seat) |
| 40 | `TokenizeThenPriceTier` *(new, SA6)* | `Usage == null` but text-length hints exist **and** a rate resolves | estimate tokens via `ITokenizer` → price at Tier-2 math; sets `TokenizerId` |
| 90 | `UnpricedFallbackTier` *(exists)* | nothing else matched | honest `$0`, `Tier="Unpriced"` |

`CoarseUnitCostTier` self-guards `Granularity==Token → null`; `PriceMapCostTier` gains
`Granularity!=Token → null` (defense in depth) so token and coarse paths never cross.

---

## 4. Catalog resolution (composite key + date-effective)

`PriceCatalog` stores `List<ModelRate>` variants (not one-per-model). Back-compat: a flat seed
entry with no dims is the **base** variant (`ContextTier.Any`, `IsBatch=false`, dims null).

```
Resolve(span):
  model   = span.ResponseModel ?? span.RequestModel;      if null → null
  when    = span.StartTime (date)
  ctxTier = (rate.LongContextThresholdTokens is T && span.Usage.InputTokens > T) ? Long : Standard
  candidates = rates.where(r.Model==model
                 && (r.EffectiveFrom is null || r.EffectiveFrom <= when)
                 && (r.EffectiveTo   is null || when < r.EffectiveTo)
                 && dimMatches(r, span))         // each non-null dim on r must equal span's; null = wildcard
  score each candidate = count of non-null dims that matched (specificity)
  return highest-score candidate (ties → newest EffectiveFrom); null if none
```

Date-effective guarantees **historical recompute**: re-costing an event at its original
`StartTime` selects the rate whose window contained it, even after a newer rate is added.

---

## 5. Catalog sources

- **`OfflineBundleCatalogSource` (exists, extended):** parses `models[]`, plus optional
  `unit_rates[]`, `tool_surcharges{}`, and per-rate `effective_from`/`effective_to`/`context_tier`/
  `is_batch`/`service_tier`/`region`/`mode`/`hourly_rate`/`multiplier`/`long_context_threshold`/
  `audio_per_token`/`image_per_token`. Stamps `SourceId` on each rate. `Seed()` stays the unsigned
  dev path (flagged).
- **`LiteLLMCatalogSource` (new):** parses BerriAI/litellm `model_prices_and_context_window.json`
  field names → `ModelRate`:

  | LiteLLM field | → ModelRate |
  |---|---|
  | `input_cost_per_token` | `InputPerToken` |
  | `output_cost_per_token` | `OutputPerToken` |
  | `cache_read_input_token_cost` | `CacheReadPerToken` |
  | `cache_creation_input_token_cost` | `CacheCreationPerToken` |
  | `output_cost_per_reasoning_token` | `ReasoningPerToken` |
  | `input_cost_per_token_above_200k_tokens` (or `_above_128k…`) | long-context variant + `LongContextThresholdTokens` |
  | `mode` | capability flag (kept in metadata; not priced) |

- **`HttpCatalogSource` (new):** `HttpClient` with an **injected `HttpMessageHandler`**; fetches a
  LiteLLM-format JSON over HTTP and delegates to the LiteLLM parser. Tests use a fake handler
  returning a canned body — **no real network**. (Google/Azure/AWS SKU-shape parsers noted as
  future; the injected-handler seam is the testable part.)
- **Signed offline bundle (D6):** `IBundleVerifier` impl `EcdsaBundleVerifier` verifies a detached
  ECDSA-P256-over-SHA256 signature against a configured public key; a tampered bundle throws. The
  verified bundle's SHA-256 is threaded into `RateSnapshot.CatalogDigest`. `Seed()` remains unsigned
  (dev only, flagged).

---

## 6. Wire flow

| Dimension | gen_ai.* / OpenInference | `aiusage.*` extension (pinned in `SemConv.cs`) | Ingest DTO field |
|---|---|---|---|
| service tier | `gen_ai.request.service_tier` (where present) | `aiusage.service_tier` | `service_tier` |
| batch | — | `aiusage.batch` (bool) | `is_batch` |
| region | `cloud.region` (resource attr) | `aiusage.region` | `region` |
| deployment type | — | `aiusage.deployment_type` | `deployment_type` |
| tool calls | `gen_ai.tool.name` per execute_tool span | `aiusage.tool_calls` (json) | `tool_calls` |
| tokenizer | — | `aiusage.tokenizer` | `tokenizer` |
| granularity/units | — | `aiusage.granularity`/`.units_consumed`/`.unit_type` | `granularity`/`units_consumed`/`unit_type` (exist) |

`GenAiSpanMapper.Map` reads these with **Token/null defaults**, so existing OTLP goldens are
untouched. `IngestionService` copies the DTO fields onto the `Span`.

---

## 7. The complete §5 gotcha golden matrix (acceptance spec)

Seed rates (`claude-opus-5`): in `5e-6`, out `25e-6`, cacheRead `5e-7`, cacheCreate `6.25e-6`.

| # | Gotcha | Status | Golden case — input → **hand-computed expected** |
|---|---|---|---|
| 1 | subset-vs-additive tokens | **COVERED** (Phase 2 normalizer goldens) | additive: 200+600+200 → in 1000; subset: 1000 w/ 600 cache → uncached 400 |
| 2 | multiple input rates at once | **COVERED** (`Cache_read_and_creation_priced_separately`) | 200·5e-6 + 600·5e-7 + 200·6.25e-6 = **0.00255** |
| 3 | reasoning bills at output | **COVERED** (`Reasoning_tokens_billed_at_output_rate`) | (1000−400)·25e-6 + 400·25e-6 = **0.025** |
| 4 | batch ~50% off | **NEW** | opus-5 batch variant out `12.5e-6`; 1000 in/500 out batch → 1000·2.5e-6 + 500·12.5e-6 = **0.00875** (½ of 0.0175) |
| 5 | tiered/context (whole request) | **NEW** | threshold 200k, long-out `37.5e-6`; input 201000 → **whole** request re-rated long: 201000·7.5e-6 + 500·37.5e-6 = **1.526250**; input 199000 → base |
| 6 | modality priced differently | **NEW** | audioPerToken `100e-6`; 1000 audio + 0 text → 1000·100e-6 = **0.10**, as its own component |
| 7 | tool surcharges stack | **NEW** | chat (0.0175 tokens) + 3 web_search @ $0.01/call → 0.0175 + 3·0.01 = **0.0475**; surcharge components present |
| 8 | non-token regimes (per_hour/unit/request) | **NEW** | PTU `HourlyRate=$5`, span 2.5h → **12.50** regardless of tokens; per_unit/request/seat via Tier-30 (#15) |
| 9 | geo/tier/residency multipliers | **NEW** | us-geo `Multiplier=1.1`; base 0.0175 → 0.0175·1.1 = **0.01925** |
| 10 | tokenizer drift @ constant rate | **NEW** (SA5) | out 1000 vs 1300 same rate → 0.025 vs 0.0325; `RateSnapshot.Rate` equal, `TokenizerId` differs |
| 11 | cloud-set third-party pricing/region | **NEW** | Bedrock claude in `us-east-1` vs `eu` variants resolve different rates via region key |
| 12 | reporting APIs lag/aggregate | **N/A Phase 4** (reconciliation) | — |
| 13 | provider response quirks | **N/A Phase 2/5** (parsing) | — |
| 14 | date-effective / historical recompute | **NEW** (SA5) | rate out 25e-6→30e-6 across a version boundary; `RecomputeFromSnapshot`=**0.0175** (original), `RecomputeFromLiveCatalog`=**0.020** |
| 15 | coarse surfaces (credits/seats/requests) | **NEW** (SA4) | UiPath Credit 2×$0.20=**0.40**; Copilot Request 5×$0.04=**0.20**; seat 3×$19=**57.00**; token span unaffected |

---

## 8. Implementation order (verified increments, commit each)

1. **Snapshotting (SA5)** — `RateSnapshot`, `CostBreakdown.RateSnapshot`, `ModelRate` provenance
   fields, `PriceMapCostTier` clock overload + snapshot population, `SnapshotRecompute.cs`,
   `ICostRecomputer`, `/v1/spans/{id}/recompute`. Gotchas #14, #10. *(complete design in hand)*
2. **Per-granularity (SA4)** — `PricingMode`, `UnitRate`, `CoarseUnitCostTier` (Order 30),
   `PriceCatalog.ResolveUnit`, `unit_rates` seed block. Gotchas #15, #8(unit/request/seat).
   *(complete design in hand)*
3. **Composite-key + date-effective catalog** — `ModelRate` dim fields, `PriceCatalog` variant
   store + specificity/date resolution, seed variants. Gotchas #4, #5, #9(variant), #11, #14(select).
4. **In-tier additive + modes** — `PriceMapCostTier`: modality (#6), `Multiplier` (#9), `PerHour`
   branch (#8), tool surcharges (#7) via `ToolCalls` + `catalog.ToolSurcharge`.
5. **Catalog sources + Tier-3 + signed bundle** — `LiteLLMCatalogSource`, `HttpCatalogSource`
   (injected handler), `ITokenizer`+`HeuristicTokenizer`+`TokenizeThenPriceTier` (Order 40),
   `IBundleVerifier`+`EcdsaBundleVerifier`. §4.2/4.3, #1 seed.
6. **Wire + docs** — `SemConv.cs` `aiusage.*` keys, `GenAiSpanMapper` + DTO fields, update
   `DEVELOPMENT_PLAN.md` Phase 3 checklist + `PROJECT_CONTEXT.md` + Decision Log.

Each increment: edit → `dotnet test -warnaserror` green → commit on `phase-3-cost-engine`.

## 9. Risks / open questions
- **Modality double-count:** `NormalizedUsage.Audio/ImageTokens` must be *disjoint* from the text
  buckets or they double-count. The normalizer (Phase 2 keystone) owns disjointness; Phase-3 tests
  use spans where modality and text buckets don't overlap and document the caveat.
- **Tool-surcharge recompute:** reproduced from `Span.ToolCalls` + snapshot surcharge rates; both
  persist, so recompute is stable (LD-4).
- **Copilot overage / seat periodicity:** the *adapter* (Phase 5) must set `UnitsConsumed` =
  billable overage and emit one seat span per period; the engine prices what it's given.
