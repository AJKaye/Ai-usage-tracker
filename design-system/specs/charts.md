# Chart & Data-Visualization Rules

> The form heuristic, mark specs, stat-tile contract, and dashboard layout for every visualization in the product. This is the applied `dataviz` skill; its palette lives in `tokens/chart-palette.json` and its chrome tokens in `dist/tokens.css` (`--chart-*`). **Invoke the `dataviz` skill for any new chart** — this file is the product-specific contract on top of it.

## Order of operations (do NOT pick color first)

1. Pick the **form** (below). 2. Assign color **by job**. 3. Validate the palette (`scripts/validate_palette.mjs`). 4. Apply mark specs. 5. Add the hover layer (default). 6. Accessibility pass (labels/table-twin). 7. Render and eyeball.

## Step 0 — is it even a chart?

| Data | Use | Not |
|------|-----|-----|
| One current value (+ trend) | **stat tile** (value + delta + sparkline) | a one-bar bar chart |
| A handful of headline numbers | **KPI row** of stat tiles | a grouped bar chart |
| The number the dashboard leads with | **hero figure** (≥48px, `--hero-figure-size`) | — |
| A single ratio vs a limit | **meter** (same-ramp track) | a 2-slice pie |
| >7 meaningful classes | a **table** (or table + chart) | more colors |

## Step 1 — job → form → color job

| Reader's job | Form | Color |
|--------------|------|-------|
| Compare magnitude | bar/column; heatmap for a grid | sequential (one hue) |
| Trend over time | line; area for a single series | sequential or 1 categorical |
| Tell series apart | grouped/stacked bar, multi-line | **categorical** |
| One series is the point | **emphasis** (highlight one, gray rest) | 1 hue + gray |
| Above/below baseline; Δ to target | diverging bar / line vs baseline | diverging |
| Part-to-whole | **stacked bar** (horizontal if many/long-named) | categorical |
| Ordered-scale share (sentiment, tiers) | **diverging stacked bar** centered on neutral | diverging |

**Sequential (one hue) is the safe default** — reach for it unless the job is specifically identity or polarity. **Emphasis** (accent one series, gray the rest) is the most under-used and often the honest answer to "make this clearer." Never solve "too many series" by generating more hues.

## Series-count ladder (categorical)

| Series | Treatment |
|--------|-----------|
| 1–3 | color alone is fine; direct-label. **1 series → no legend box** (title names it). |
| 4 | direct labels become **mandatory**; all-pairs forms (scatter/bubble/choropleth/small-multiples) **cap at 3** — fold to "Other" or facet. |
| 5–6 | legend or small multiples. |
| 7–8 | token ceiling. Past 8: fold tail to "Other", facet, or composite-encode (hue × shape). |

Assign categorical slots **in fixed order** (`--chart-cat-1` … `-8`) — the order is the color-blind-safety mechanism; never reorder without re-validating.

## Mark specs (fixed across every chart)

| Mark | Spec |
|------|------|
| Bar/column | **≤24px thick** (cap it — leftover band is air); **4px rounded data-end, square at baseline** |
| Line | **2px**, round join/cap |
| Marker/end-dot | **≥8px** (r≥4), filled with series color, **2px surface-color ring** so it stays legible over overlaps |
| Area fill | series hue at **~10% opacity** (a wash, never a saturated block) |
| Gridlines/axes | `--chart-gridline` / `--chart-baseline`, **hairline 1px solid** (never dashed), recessive |
| Surface gap | **2px gap in the surface color** (`--chart-surface-gap`) separates touching marks — white separates, never a stroke |

**One axis only — never dual-axis** (use two charts, small multiples, or index to base=100). **Text never wears the data color** — labels/values/axes use `--chart-ink-*`; a data hue is only for marks (exception: a label *inside* a colored fill picks white/ink by luminance).

## Labels & legend

- Legend present for **≥2 series** (the dependable identity channel); direct labels supplement, not replace.
- **Label selectively** — endpoint / extreme / the one series the story is about. Never a number on every point; axis/legend/tooltip/table carry the rest.
- Priority: direct labels → gridlines → (only then) a second encoding. Measure before placing a label inside a bar; if it doesn't fit, move outside or drop to tooltip. Never crop with `overflow:hidden`.

## Tooltips & hover (interactive by default)

- Tooltips **enhance, never gate** — every value is also in a direct label or the table view; **keyboard focus shows the same as hover**.
- Line/area: a **crosshair** vertical hairline snaps to the nearest X; **one tooltip lists every series** at that X. Bars/cells: the mark is the hit target (no crosshair); hovered mark lifts slightly.
- In the tooltip, **values lead** (high-contrast), series name is secondary; rows use **line keys** (a short stroke), not filled boxes.
- **Tooltip label text is untrusted data → insert via `textContent`/`createTextNode`, never `innerHTML`.**
- Hit target bigger than the mark; scatter dots need a ≥24px transparent hit area (or a Voronoi/nearest-point layer).

## Stat tile / KPI / meter / sparkline

- **Stat tile:** `label` (sentence case, no trailing colon) · `value` (sans semibold, auto-compact) · optional `delta` (signed, vs a named period, color = direction × whether up is good, using `--chart-delta-good` for good-up) · optional 12-point `sparkline` (de-emphasis hue, current period in accent).
- **KPI row:** a row of stat tiles — the right form for "a handful of headline numbers."
- **Meter:** fill carries severity (accent → warning → danger); **unfilled track is a lighter step of the same ramp** so state reads across the whole bar.
- **Hero figure:** the one number a view leads with, ≥48px, system sans, **exactly one per view**.

## Dashboard layout

- **Filters in one row, above the charts**, left-aligned; **date range first** (presets as rows: today / 7 / 30 / 90 / MTD, then custom). Never per-chart, never inside a chart card.
- **Filters scope everything below** — every chart/stat/table re-renders against the same slice so numbers agree.
- **Refetch keeps the frame** — hold the previous render at reduced opacity while reloading; no skeleton, no layout jump, no flash.
- Chart container is a `<figure>`/card that owns responsive sizing, title/caption, and the **table-view toggle** (the accessibility twin). Any fixed height must include the x-axis band so the card never gets a nested scrollbar.

## Light + dark + accessibility

- Dark mode is **selected**, with its own validated steps — declared under both the OS media query and the `[data-theme]` toggle (the toggle wins). Chrome + categorical dark values are already in `dist/tokens.css`.
- **Contrast:** marks ≥3:1 vs surface; sub-3:1 is a documented **relief** that *obligates* visible direct labels or the table view (not dismissable). Ordinal ramp's surface-nearest step ≥2:1.
- **Color-blind safety:** never eyeballed — run the validator (CVD ΔE ≥8 target / ≥6 floor-with-secondary-encoding; normal-vision floor ≥15 hard gate).
- **Non-color identity channel:** legend always present ≥2 series; ≤4 also direct-labeled; optional **texture fill** ("Lines" at 45°/135° only, tone-on-tone, ordered on value scales) triggered by the accessibility setting, print, or `forced-colors`.
- **Status never by color alone** — always icon + label. **Everything (incl. hero) stays in the system sans** — no display/serif face anywhere in data-viz.

## Validator

`node scripts/validate_palette.mjs "<csv of hexes>" --mode light|dark [--surface #hex] [--pairs adjacent|all] [--ordinal]`. Exit 0 = pass (WARN bands still 0, but legal only with the secondary encoding they name); exit 1 = hard fail; exit 2 = usage error. Runs in CI on the default template and every tenant palette. **Don't run it on sequential/diverging ramps without `--ordinal`** — they fail the categorical checks by design.
