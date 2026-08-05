/* =============================================================================
 * AI Usage Tracker — Design Tokens (TypeScript)
 * GENERATED ARTIFACT — emitted by Style Dictionary from design-system/tokens/**.
 * This hand-authored seed mirrors dist/tokens.css and is safe to consume until
 * the build pipeline lands (see scripts/build.md).
 *
 * USAGE IN REACT
 *   Prefer the CSS custom properties (dist/tokens.css) for actual styling — they
 *   are what makes theming "change once, applies everywhere". Use these TS consts
 *   for: type-safe token *references* in CSS-in-JS (`var(TOKEN.color.bg.surface)`),
 *   passing token names to a charting lib, and compile-time autocomplete.
 *   Do NOT hardcode the raw hex from PRIMITIVES into components — reference ROLE
 *   tokens by their CSS var name so a theme swap still cascades.
 * ========================================================================== */

/** A CSS custom-property reference, e.g. "var(--color-bg-surface)". */
export type TokenRef = `var(--${string})`;

const ref = (name: string): TokenRef => `var(--${name})`;

/** Role tokens — reference these in components (they re-point per theme/mode). */
export const TOKEN = {
  color: {
    bg: {
      canvas: ref("color-bg-canvas"),
      surface: ref("color-bg-surface"),
      surfaceRaised: ref("color-bg-surface-raised"),
      surfaceSunken: ref("color-bg-surface-sunken"),
      subtle: ref("color-bg-subtle"),
      muted: ref("color-bg-muted"),
      inverse: ref("color-bg-inverse"),
      scrim: ref("color-bg-scrim"),
    },
    text: {
      primary: ref("color-text-primary"),
      secondary: ref("color-text-secondary"),
      muted: ref("color-text-muted"),
      disabled: ref("color-text-disabled"),
      inverse: ref("color-text-inverse"),
      onBrand: ref("color-text-on-brand"),
      link: ref("color-text-link"),
    },
    border: {
      subtle: ref("color-border-subtle"),
      default: ref("color-border-default"),
      strong: ref("color-border-strong"),
      focus: ref("color-border-focus"),
      brand: ref("color-border-brand"),
    },
    brand: {
      solid: ref("color-brand-solid"),
      solidHover: ref("color-brand-solid-hover"),
      solidActive: ref("color-brand-solid-active"),
      subtle: ref("color-brand-subtle"),
      text: ref("color-brand-text"),
    },
    feedback: {
      successSubtle: ref("color-feedback-success-subtle"),
      successFg: ref("color-feedback-success-fg"),
      successSolid: ref("color-feedback-success-solid"),
      warningSubtle: ref("color-feedback-warning-subtle"),
      warningFg: ref("color-feedback-warning-fg"),
      warningSolid: ref("color-feedback-warning-solid"),
      dangerSubtle: ref("color-feedback-danger-subtle"),
      dangerFg: ref("color-feedback-danger-fg"),
      dangerSolid: ref("color-feedback-danger-solid"),
      infoSubtle: ref("color-feedback-info-subtle"),
      infoFg: ref("color-feedback-info-fg"),
    },
  },
  space: {
    "0": ref("space-0"), "1": ref("space-1"), "2": ref("space-2"),
    "3": ref("space-3"), "4": ref("space-4"), "5": ref("space-5"),
    "6": ref("space-6"), "7": ref("space-7"), "8": ref("space-8"),
    "9": ref("space-9"), "10": ref("space-10"), "11": ref("space-11"),
    "12": ref("space-12"),
  },
  radius: {
    none: ref("radius-none"), xs: ref("radius-xs"), sm: ref("radius-sm"),
    md: ref("radius-md"), lg: ref("radius-lg"), xl: ref("radius-xl"),
    "2xl": ref("radius-2xl"), full: ref("radius-full"),
  },
  font: {
    sans: ref("font-sans"), mono: ref("font-mono"), display: ref("font-display"),
  },
  shadow: {
    "0": ref("shadow-0"), "1": ref("shadow-1"), "2": ref("shadow-2"),
    "3": ref("shadow-3"), "4": ref("shadow-4"),
  },
  motion: {
    durInstant: ref("dur-instant"), durMicro: ref("dur-micro"),
    durFast: ref("dur-fast"), durBase: ref("dur-base"), durSlow: ref("dur-slow"),
    easeStandard: ref("ease-standard"), easeOut: ref("ease-out"),
    easeIn: ref("ease-in"), easeEmphasized: ref("ease-emphasized"),
  },
} as const;

/** Chart chrome tokens (mode-aware via CSS; same names light/dark). */
export const CHART_CHROME = {
  surface: ref("chart-surface"),
  plane: ref("chart-plane"),
  inkPrimary: ref("chart-ink-primary"),
  inkSecondary: ref("chart-ink-secondary"),
  inkMuted: ref("chart-ink-muted"),
  gridline: ref("chart-gridline"),
  baseline: ref("chart-baseline"),
  deltaGood: ref("chart-delta-good"),
  border: ref("chart-border"),
  divergingMid: ref("chart-diverging-mid"),
  surfaceGap: ref("chart-surface-gap"),
} as const;

/** Categorical chart slots 1..8 (assign in order, never cycle past 8). */
export const CHART_CATEGORICAL: readonly TokenRef[] = [
  ref("chart-cat-1"), ref("chart-cat-2"), ref("chart-cat-3"), ref("chart-cat-4"),
  ref("chart-cat-5"), ref("chart-cat-6"), ref("chart-cat-7"), ref("chart-cat-8"),
] as const;

/** Fixed status scale — never themed; ALWAYS render with an icon + label too. */
export const CHART_STATUS = {
  good: ref("chart-status-good"),
  warning: ref("chart-status-warning"),
  serious: ref("chart-status-serious"),
  critical: ref("chart-status-critical"),
} as const;

/**
 * RAW default palette hex (light) — for feeding a canvas/SVG charting lib that
 * cannot read CSS vars, and for the palette validator. Keep in sync with the
 * validated template in tokens/chart-palette.json. Prefer CHART_CATEGORICAL
 * (CSS vars) whenever the renderer can resolve them.
 */
export const CHART_CATEGORICAL_HEX = {
  light: ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"],
  dark:  ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"],
} as const;

export type ThemeName = "base" | "dark" | "example-ssc" | (string & {});

/** Apply a theme by setting data-theme on <html>. `null` -> base/system. */
export function applyTheme(theme: ThemeName | null): void {
  const el = document.documentElement;
  if (theme && theme !== "base") el.setAttribute("data-theme", theme);
  else el.removeAttribute("data-theme");
}
