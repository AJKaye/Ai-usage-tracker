# Component Rules — States, Density, Tables

> The interaction-state matrix and data-density rules every component follows. Grounded in `web-design-guidelines/.../web-interface-guidelines.md`, `ui-ux-pro-max/references/quick-reference.md` §2/§7/§8, `pro-rules.md`, and `design/subskills/architectural/dashboard/live/references/components.md`. All values reference tokens from `dist/tokens.css`.

## The state matrix (every interactive component implements all of these)

| State | Rule | Tokens |
|-------|------|--------|
| **Rest** | baseline | component tokens |
| **Hover** | more prominent than rest; **background/opacity swap only — never a transform** that shifts neighbours; feedback within ~100ms | `--color-bg-subtle`, `--btn-primary-bg-hover` |
| **Active/pressed** | distinct from hover; color/opacity/elevation change, not layout | `--btn-primary-bg-active` |
| **Focus** | visible ring, `:focus-visible` | `--focus-ring-*` (see accessibility.md) |
| **Disabled** | opacity 0.38–0.5 + `cursor` change + the semantic disabled attribute; never "looks tappable, does nothing" | `--color-text-disabled` |
| **Read-only** | visually **and** semantically distinct from disabled | — |
| **Loading** | skeleton/shimmer for ops >300ms; skeleton over blocking spinner for >1s; loading text ends with `…` | — |
| **Empty** | helpful message + action; never render broken UI for empty arrays/strings | — |
| **Error** | inline near the field + recovery path (retry/edit); `role="alert"`/`aria-live` | `--color-feedback-danger-*` |

**Async buttons:** stay enabled until the request starts, then show a spinner; for live-polling primaries, gate taps with a JS `busy` flag rather than `[disabled]` so the visual stays identical.

## Data-dense tables

- Numeric columns use `font-variant-numeric: tabular-nums` (prevents layout shift), `font-weight: 600`, `letter-spacing: var(--ls-tight)` on values.
- Density: dashboard rhythm is the dense end of the spacing ramp (`--space-3`..`--space-8`, i.e. 8–32px). Keep the 4/8 rhythm.
- Row hover = **background swap only** (`--table-row-hover`); no transform/shadow.
- Changed/live rows flash via `--table-row-flash` (keyframe ~1.4s), gated by `prefers-reduced-motion`.
- Large lists (>50 rows): virtualize (`content-visibility: auto` or a virtual list). 1000+ data points: aggregate/sample with drill-down.
- Truncation: flex children need `min-width: 0`; prefer wrapping; when truncating, expose full text via tooltip/expand — never `overflow: hidden` to silently crop.

## Status representation (constrain the vocabulary)

Map any project state into a **fixed small set** of status chips (e.g. Done / In progress / Blocked / In review / To do), each with a fixed bg/fg pair from `--color-feedback-*`, always **icon + label** (never color alone). Do not invent extra states per screen — map into the canonical set.

## Number formatting

- Big standalone numbers (hero, stat-tile values) use **proportional** figures + auto-compaction (`1,284` / `12.9K` / `$4.2M`).
- Reserve `tabular-nums` for columns/axes that must align vertically. Tabular on a lone big number makes it look loose.

## Numbers that must agree

Filters scope everything below them, so every card/stat/table re-renders against the same slice — numbers on one screen never disagree. (See charts.md → dashboard layout.)
