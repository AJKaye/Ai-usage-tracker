# Token Architecture & Authoring Rules

> How the token system is layered, named, changed, and extended per tenant. This is the contract behind "change once → applies everywhere." Grounded in `design/subskills/architectural/figma-generate-library/references/{token-creation,naming-conventions}.md` and `color-expert/.../designbook-reactive-design-token-spec.md`.

## The three layers

| Layer | File | Holds | Consumed by |
|-------|------|-------|-------------|
| **Primitive** | `tokens/primitive.tokens.json` | raw scales (`color.gray.500`, `space.5`, `radius.lg`) — brand-neutral, single-mode | **only** the semantic layer |
| **Semantic** | `tokens/semantic.tokens.json` | role aliases (`color.bg.surface`, `color.text.primary`) carrying **light + dark** modes | components + chart specs |
| **Component** | `tokens/component.tokens.json` | per-component knobs (`button.primary.bg`, `table.row-hover`) | that component's styles |

**The hard rule (non-negotiable):** semantic and component tokens **never** hold a raw hex/px — they always `{reference}` a primitive (or a semantic, for component tokens). Need a new value? Add the primitive first, then alias it. This is what lets you regenerate the palette or swap a theme and have every layer above recompute untouched.

## Naming → CSS variable

- Logical path `{category}.{subcategory}.{role}` → CSS `--category-subcategory-role`. Example: `color.bg.surface` → `var(--color-bg-surface)`.
- Primitives are flat `{family}.{step}`: `color.brand.600` → `var(--color-brand-600)`.
- A tenant prefix (if ever needed) goes in the **CSS name only**, never the logical path.

## How a component consumes tokens (the only correct pattern)

```css
.btn-primary {
  background: var(--btn-primary-bg);      /* component token */
  color: var(--btn-primary-fg);
  border-radius: var(--btn-radius);
  padding: var(--btn-pad-y) var(--btn-pad-x);
  font-weight: var(--btn-font-weight);
  transition: background var(--dur-fast) var(--ease-standard);
}
.btn-primary:hover  { background: var(--btn-primary-bg-hover); }
.btn-primary:active { background: var(--btn-primary-bg-active); }
.btn-primary:focus-visible {
  outline: var(--focus-ring-width) solid var(--focus-ring-color);
  outline-offset: var(--focus-ring-offset);
}
```

Never write `background: #2563eb` or `padding: 12px` in a component. The stylelint gate (see `DESIGN_SYSTEM.md` → Enforcement) fails the build on a raw hex/px in component styles.

## How to change a design decision globally

1. **A brand color / spacing step / radius everywhere** → edit the value in `primitive.tokens.json`, rebuild `dist/`. Every semantic + component + CSS var recomputes. One edit, whole app.
2. **What a role means** (e.g. make `bg.surface` slightly warmer) → edit the alias target in `semantic.tokens.json`.
3. **One component only** → edit `component.tokens.json`.
4. **Dark-mode value of a role** → edit the `$extensions.mode.dark` entry on that semantic token.

## How to add a tenant (white-label)

The whole white-label surface is ~a dozen values (the "extended/inherited theme" pattern from `wwds-variables.md`).

1. Copy `tokens/themes/_template.json` → `tokens/themes/<tenant>.json`.
2. Fill `brand.ramp` (11 steps of the tenant's primary hue). Keep step **600** as the on-white solid and ensure step **700** clears AA (4.5:1) for small text.
3. Optionally set `font.display` / `font.sans`, `radius.control` / `radius.input`, `brand.accent`. Leave `null` to inherit.
4. Chart palette: leave `chart.palette: "inherit"` to keep the accessibility-safe default, **or** supply 8 light + 8 dark categorical hexes and run the validator (step 5).
5. If you set a chart palette: `node scripts/validate_palette.mjs "<light csv>" --mode light` and `--mode dark --surface <dark chart surface>`. Fix any FAIL with snap-to-passing (hold hue, nudge lightness one step, re-run). CI runs this automatically.
6. Rebuild `dist/`; the build emits a `:root[data-theme="<tenant>"]` scope. Apply at runtime with `applyTheme("<tenant>")` (see `dist/tokens.ts`) or `<html data-theme="<tenant>">`.

`tokens/themes/example-ssc.json` is a worked example (SS&C) — read it as the reference.

## The three gaps this system fills (vs the source brand kits)

1. **Dark mode** — authored from scratch as its own set of semantic values (not an inversion); source kits were light-only.
2. **Elevation** — a 5-step `shadow.*` scale (source kits had none); dark uses stronger, tonal-surface-backed shadows since drop shadows read weakly on dark.
3. **Spacing** — one canonical 4/8 ramp reconciling the source kit's split 11-step JSON / 7-step CSS scales.
