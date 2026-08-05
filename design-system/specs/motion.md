# Motion Rules

> When and how the UI animates. Grounded in `design-motion-principles/SKILL.md` + `references/{accessibility,performance}.md` and `web-interface-guidelines.md` "Animation". All durations/easings reference `--dur-*` / `--ease-*` in `dist/tokens.css`. **This is a data-dense productivity app → bias to fast/subtle/instant.**

## Duration & easing tokens

| Token | Value | Use |
|-------|-------|-----|
| `--dur-micro` (120ms) | micro | hover, small state changes |
| `--dur-fast` (180ms) | fast | most UI transitions (the default for this app) |
| `--dur-base` (240ms) | base | larger transitions, overlays |
| `--dur-slow` (320ms) | slow | rare, deliberate emphasis |
| `--ease-out` | entering | elements appearing |
| `--ease-in` | exiting | elements leaving |
| `--ease-standard` | in-place | color/opacity changes |

Rules of thumb: micro-interactions 150–300ms; complex transitions ≤400ms; avoid >500ms. **Exit ≈60–70% of enter duration.** List/grid stagger 30–50ms/item. Never `linear` for UI transitions.

## The frequency gate (decides whether to animate at all)

Rare action → expressive is OK. Daily → subtle + fast. 100s/day → instant, no animation. **Keyboard-initiated → never animate.** "The best animation goes unnoticed." In a dashboard people live in, most interactions are daily-or-more → default to fast/subtle, and animate **1–2 key elements per view max**.

## What to animate

- Every animation must express **cause/effect**, not decoration. State changes animate smoothly (crossfade for content replacement in the same container), not snap.
- Animations are **interruptible** and never block input mid-flight.
- **Never cause layout reflow/CLS** — animate position with `transform`, not `top`/`left`/`margin`.

## Reduced motion (mandatory — not optional)

The global override ships in `dist/tokens.css` (verbatim from the motion skill): under `@media (prefers-reduced-motion: reduce)` it sets `animation-duration`/`transition-duration` to `0.01ms !important` and `animation-iteration-count: 1` on `*`. This disables motion while **preserving end states** so nothing breaks.

- Functional-vs-decorative test: if removing the animation breaks understanding of *what happened*, provide an instant non-motion alternative; otherwise remove it.
- Avoid vestibular triggers (full-screen transitions, parallax, zoom, spin). Any looping/ambient motion (e.g. a live-status pulse) must be pausable **and** disabled under reduced-motion.

## Performance — compositor-only

- Animate **`transform` and `opacity` only.** Never `transition: all` — list properties explicitly.
- Do **not** animate layout-triggering props (`width`, `height`, `top/left`, `margin`, `padding`, `font-size`). Use `transform: scale()` / `translate()` instead.
- `will-change`: hint only the specific prop about to animate, never globally. GPU-layer budget: 0–3 animated elements fine; 4–10 test on low-end; 10+ reconsider (virtualize/stagger).
- SVG/chart transforms: apply on a `<g>` wrapper with `transform-box: fill-box; transform-origin: center`.
- Chart entrance animations respect reduced-motion **and** the data must be readable immediately (never gate comprehension behind an animation).
