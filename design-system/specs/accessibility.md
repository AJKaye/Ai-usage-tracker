# Accessibility Rules (WCAG 2.1 AA)

> Mandatory across every feature. Grounded in `ui-ux-pro-max/references/{quick-reference,pro-rules}.md`, `web-design-guidelines/references/web-interface-guidelines.md`, and `design-motion-principles/references/accessibility.md`. Verified by the axe + keyboard + contrast gates in CI.

## Contrast (measured in both light AND dark independently)

| Content | Min ratio |
|---------|-----------|
| Normal text | **4.5:1** |
| Large text (≥24px, or ≥19px bold) | **3:1** |
| UI components / non-text (borders, control boundaries, icons carrying meaning) | **3:1** |
| Data-viz **marks** vs chart surface | **3:1** (sub-3:1 = documented "relief" → obligates direct labels or the table view) |
| Data-viz **text labels** | **4.5:1** |
| Gridlines | intentionally low-contrast so they recede |

Token pairs are pre-checked: `--color-text-primary/secondary/muted` clear their thresholds on `--color-bg-surface` in **both** modes. Never assume a light-mode pass carries to dark — the dark scope is validated separately.

## Focus

- Every interactive element has a **visible focus ring, 2–4px** → use `--focus-ring-width` + `--focus-ring-color` (brand hue, lighter in dark).
- Use `:focus-visible` (not `:focus`) so a ring doesn't show on mouse click; `:focus-within` for compound controls.
- **Never** `outline: none` without a replacement ring.
- After a route change, move focus to the main content region. Provide a skip-to-content link.

## Keyboard

- Tab order matches visual order; everything actionable is reachable and operable by keyboard.
- Use `<button>` for actions, `<a>`/`<Link>` for navigation — never `<div onClick>`.
- Charts: points/bars/slices are keyboard-navigable; tooltips are keyboard-reachable (focus shows the same as hover), not hover-only; sortable tables expose `aria-sort`.

## Hit targets

- **44×44px** minimum for primary/touch controls; 8px minimum gap. Expand the hit area beyond the visual glyph when smaller. (Desktop dense tables: treat 44px as the floor for primary controls, not a mandate for every cell.)

## Forms & errors

- Visible `<label>` per input (not placeholder-only); label is clickable. Correct `type`/`inputmode`; `autocomplete` + meaningful `name`. Never block paste.
- Validate **on blur**, not per keystroke. Errors inline next to the field **and** a summary at top (with anchor links) when there are several.
- Error copy states **cause + fix**, never just "Invalid input."
- On submit error, auto-focus the first invalid field.
- Screen-reader signaling: field errors via `role="alert"` / `aria-live`; toasts `aria-live="polite"` and must not steal focus.
- Submit button stays enabled until the request starts, then shows a spinner.

## Color independence

- **Never convey information by color alone** — pair with icon, text, or pattern. This is why the chart **status** scale is always rendered as **icon + label**, and why identity in charts is backed by a legend (+ direct labels ≤4 series) and an optional texture-fill channel.
- Establish hierarchy through size/spacing/contrast, not color alone.

## Motion

- Ship the mandatory global `prefers-reduced-motion: reduce` override (already in `dist/tokens.css`) — it zeroes durations while preserving end states so layouts never break.
- Functional-vs-decorative test: if removing an animation breaks the user's understanding of what happened, provide an instant (non-motion) alternative; if not, remove it under reduced-motion.

## Every chart has a table-view twin

The WCAG-clean accessibility twin of any visualization is a data table behind a toggle on the chart container. Tooltips never *gate* a value — it's always reachable via a label or the table.

## Known coverage gaps (verify externally if load-bearing)

The local sources don't fully specify WCAG 1.4.10 (reflow to 400% zoom), 1.4.12 (text-spacing overrides), or the full 1.4.11 non-text-contrast surface beyond the 3:1 figure. If those SC are in audit scope, pull an external WCAG reference — don't assume they're covered here.
