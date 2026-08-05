#!/usr/bin/env node
/* =============================================================================
 * validate_palette.mjs — categorical chart-palette gate
 *
 * Vendored/derived from the `dataviz` skill's validator (JS/Py twins). Enforces
 * the five computable checks on a CATEGORICAL palette so every tenant's brand
 * chart colors stay color-blind-safe and legible before they ship.
 *
 *   1. Lightness band   OKLCH L in  [0.43, 0.77] light  /  [0.48, 0.67] dark
 *   2. Chroma floor     OKLCH C >= 0.10
 *   3. CVD separation   OKLab dE*100 under Machado-2009 protan+deutan @1.0;
 *                       target >= 8, floor >= 6 (floor legal ONLY w/ secondary
 *                       encoding — a WARN, not a pass)
 *   4. Normal-vision floor  worst pair dE*100 >= 15 unsimulated (HARD gate)
 *   5. Contrast vs surface  >= 3:1 (sub-3:1 is a WARN 'relief' — obligates
 *                       visible direct labels or the table view)
 * Checks 1 (fixed hue anchors) and 6 (documented palette only) are structural.
 *
 * Sequential/diverging ramps FAIL these categorical checks BY DESIGN — do not
 * "fix" a good ramp to satisfy this tool; pass --ordinal for ordered ramps.
 *
 * EXIT CODES:  0 = no hard FAIL (WARN bands still 0)   1 = a FAIL   2 = usage error
 *
 * USAGE:
 *   node validate_palette.mjs "#2a78d6,#eb6834,..." --mode light
 *   node validate_palette.mjs "#3987e5,..." --mode dark --surface "#1a1a19"
 *   node validate_palette.mjs "#86b6ef,#5598e7,#256abf,#104281" --ordinal
 *   node validate_palette.mjs "#a,#b,#c,#d" --pairs all   # scatter/maps/small-multiples
 * ========================================================================== */

// ---- thresholds (baked, matching the dataviz validator) --------------------
const BAND = { light: [0.43, 0.77], dark: [0.48, 0.67] };
const CHROMA_FLOOR = 0.10;
const CVD_TARGET = 8.0, CVD_FLOOR = 6.0, NORMAL_FLOOR = 15.0;
const CONTRAST_MIN = 3.0;
const ORDINAL_MIN_DL = 0.06, ORDINAL_LIGHT_FLOOR = 2.0, ORDINAL_MAX_HUE_SPREAD = 40;
const DEFAULT_SURFACE = { light: "#fcfcfb", dark: "#1a1a19" };

// ---- input sanitation (kept in lockstep w/ the skill: strip NBSP/em-space) --
const HEX_RE = /^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/;
function cleanHex(s) {
  const t = s.replace(/[\s  -   　]+/g, "");
  if (!HEX_RE.test(t)) throw new Error(`invalid hex: ${JSON.stringify(s)}`);
  return t.length === 4
    ? "#" + [...t.slice(1)].map((c) => c + c).join("")
    : t.toLowerCase();
}

// ---- color math: sRGB -> linear -> OKLab/OKLCH ; WCAG contrast --------------
function srgbToLin(c) { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }
function hexToRgb(h) { return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)]; }
function relLum([r, g, b]) { const R = srgbToLin(r), G = srgbToLin(g), B = srgbToLin(b); return 0.2126 * R + 0.7152 * G + 0.0722 * B; }
function contrast(a, b) { const la = relLum(hexToRgb(a)), lb = relLum(hexToRgb(b)); const hi = Math.max(la, lb), lo = Math.min(la, lb); return (hi + 0.05) / (lo + 0.05); }

function rgbToOklab([r, g, b]) {
  const lr = srgbToLin(r), lg = srgbToLin(g), lb = srgbToLin(b);
  const l = 0.4122214708 * lr + 0.5363325363 * lg + 0.0514459929 * lb;
  const m = 0.2119034982 * lr + 0.6806995451 * lg + 0.1073969566 * lb;
  const s = 0.0883024619 * lr + 0.2817188376 * lg + 0.6299787005 * lb;
  const l_ = Math.cbrt(l), m_ = Math.cbrt(m), s_ = Math.cbrt(s);
  return [
    0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
    1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
    0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_,
  ];
}
function oklabToOklch([L, a, b]) { const C = Math.hypot(a, b); let h = Math.atan2(b, a) * 180 / Math.PI; if (h < 0) h += 360; return [L, C, h]; }
function deLab(p, q) { return Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]) * 100; }

// ---- Machado-Oliveira-Fernandes 2009 CVD matrices @ severity 1.0 -----------
const MACHADO = {
  protan: [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]],
  deutan: [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.011820, 0.042940, 0.968881]],
};
function applyCvd(rgb, m) {
  const lin = rgb.map(srgbToLin);
  const out = m.map((row) => row[0] * lin[0] + row[1] * lin[1] + row[2] * lin[2]);
  return out.map((v) => { const c = Math.max(0, Math.min(1, v)); const s = c <= 0.0031308 ? c * 12.92 : 1.055 * c ** (1 / 2.4) - 0.055; return Math.round(s * 255); });
}

// ---- arg parsing -----------------------------------------------------------
function parseArgs(argv) {
  const a = { mode: "light", pairs: "adjacent", ordinal: false, surface: null };
  const pos = [];
  for (let i = 0; i < argv.length; i++) {
    const t = argv[i];
    if (t === "--mode") a.mode = argv[++i];
    else if (t === "--pairs") a.pairs = argv[++i];
    else if (t === "--surface") a.surface = argv[++i];
    else if (t === "--ordinal") a.ordinal = true;
    else pos.push(t);
  }
  if (!["light", "dark"].includes(a.mode)) throw new Error(`--mode must be light|dark`);
  if (!["adjacent", "all"].includes(a.pairs)) throw new Error(`--pairs must be adjacent|all`);
  if (!pos.length) throw new Error("no palette given");
  a.palette = pos.join(",").split(",").map((s) => s.trim()).filter(Boolean).map(cleanHex);
  a.surface = a.surface ? cleanHex(a.surface) : DEFAULT_SURFACE[a.mode];
  return a;
}

function pairIndices(n, mode) {
  const out = [];
  if (mode === "all") { for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) out.push([i, j]); }
  else { for (let i = 0; i < n - 1; i++) out.push([i, i + 1]); }
  return out;
}

// ---- checks ----------------------------------------------------------------
function validateCategorical(a) {
  const rows = [];
  let hardFail = false, warn = false;
  const [loL, hiL] = BAND[a.mode];

  // per-color: band, chroma, contrast
  a.palette.forEach((hex, i) => {
    const rgb = hexToRgb(hex);
    const [L, C] = oklabToOklch(rgbToOklab(rgb));
    const cr = contrast(hex, a.surface);
    const bandOk = L >= loL && L <= hiL;
    const chromaOk = C >= CHROMA_FLOOR;
    const contrastOk = cr >= CONTRAST_MIN;
    if (!bandOk) hardFail = true;
    if (!chromaOk) hardFail = true;
    if (!contrastOk) warn = true; // relief: obligates labels/table, not a hard fail
    rows.push({
      check: `slot ${i + 1} ${hex}`,
      L: L.toFixed(3), C: C.toFixed(3), contrast: cr.toFixed(2) + ":1",
      status: !bandOk || !chromaOk ? "FAIL" : (!contrastOk ? "WARN(relief)" : "PASS"),
    });
  });

  // pairwise: normal-vision + CVD
  const pairs = pairIndices(a.palette.length, a.pairs);
  const rgbs = a.palette.map(hexToRgb);
  const labNormal = rgbs.map(rgbToOklab);
  const labP = rgbs.map((r) => rgbToOklab(applyCvd(r, MACHADO.protan)));
  const labD = rgbs.map((r) => rgbToOklab(applyCvd(r, MACHADO.deutan)));
  let worstNormal = Infinity, worstCvd = Infinity, worstNPair = "", worstCPair = "";
  for (const [i, j] of pairs) {
    const dn = deLab(labNormal[i], labNormal[j]);
    const dc = Math.min(deLab(labP[i], labP[j]), deLab(labD[i], labD[j]));
    if (dn < worstNormal) { worstNormal = dn; worstNPair = `${i + 1}-${j + 1}`; }
    if (dc < worstCvd) { worstCvd = dc; worstCPair = `${i + 1}-${j + 1}`; }
  }
  const normalOk = worstNormal >= NORMAL_FLOOR;
  if (!normalOk) hardFail = true;
  let cvdStatus;
  if (worstCvd >= CVD_TARGET) cvdStatus = "PASS";
  else if (worstCvd >= CVD_FLOOR) { cvdStatus = "WARN(needs 2nd encoding)"; warn = true; }
  else { cvdStatus = "FAIL"; hardFail = true; }

  rows.push({ check: `normal-vision floor (${a.pairs})`, worst: worstNormal.toFixed(1), pair: worstNPair, status: normalOk ? "PASS" : "FAIL" });
  rows.push({ check: `CVD protan/deutan (${a.pairs})`, worst: worstCvd.toFixed(1), pair: worstCPair, status: cvdStatus });
  return { rows, hardFail, warn };
}

function validateOrdinal(a) {
  const rows = [];
  let hardFail = false;
  const lch = a.palette.map((h) => oklabToOklch(rgbToOklab(hexToRgb(h))));
  // monotone lightness + step delta
  let monoOk = true, stepOk = true;
  const dir = lch.length > 1 && lch[lch.length - 1][0] < lch[0][0] ? -1 : 1;
  for (let i = 1; i < lch.length; i++) {
    const dL = (lch[i][0] - lch[i - 1][0]) * dir;
    if (dL <= 0) monoOk = false;
    if (Math.abs(lch[i][0] - lch[i - 1][0]) < ORDINAL_MIN_DL) stepOk = false;
  }
  // single hue spread
  const hues = lch.map((c) => c[2]);
  const spread = Math.max(...hues) - Math.min(...hues);
  const hueOk = spread <= ORDINAL_MAX_HUE_SPREAD;
  // light-end contrast (step nearest surface)
  const nearest = a.palette[a.mode === "dark" ? a.palette.length - 1 : 0];
  const cr = contrast(nearest, a.surface);
  const contrastOk = cr >= ORDINAL_LIGHT_FLOOR;
  if (!monoOk || !stepOk || !hueOk || !contrastOk) hardFail = true;
  rows.push({ check: "monotone lightness", status: monoOk ? "PASS" : "FAIL" });
  rows.push({ check: `min step dL>=${ORDINAL_MIN_DL}`, status: stepOk ? "PASS" : "FAIL" });
  rows.push({ check: `single hue spread<=${ORDINAL_MAX_HUE_SPREAD}deg`, spread: spread.toFixed(1), status: hueOk ? "PASS" : "FAIL" });
  rows.push({ check: `surface-end contrast>=${ORDINAL_LIGHT_FLOOR}:1`, contrast: cr.toFixed(2) + ":1", status: contrastOk ? "PASS" : "FAIL" });
  return { rows, hardFail, warn: false };
}

// ---- main ------------------------------------------------------------------
function main() {
  let a;
  try { a = parseArgs(process.argv.slice(2)); }
  catch (e) { console.error(`usage error: ${e.message}`); process.exit(2); }

  const res = a.ordinal ? validateOrdinal(a) : validateCategorical(a);
  console.log(`\nPalette gate — mode=${a.mode} surface=${a.surface} pairs=${a.pairs}${a.ordinal ? " ordinal" : ""}`);
  console.table(res.rows);
  if (res.hardFail) { console.error("RESULT: FAIL (hard gate) — palette must change before shipping."); process.exit(1); }
  if (res.warn) { console.warn("RESULT: PASS with WARN — legal ONLY with secondary encoding (labels/table/icon+label)."); process.exit(0); }
  console.log("RESULT: PASS."); process.exit(0);
}
main();
