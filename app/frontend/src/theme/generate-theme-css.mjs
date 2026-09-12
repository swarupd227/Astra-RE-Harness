/**
 * Emits src/theme/theme.css from src/theme/palette.json.
 *
 *   node src/theme/generate-theme-css.mjs      (or: npm run theme:css)
 *
 * palette.json is the single source of truth for every colour token. The
 * CSS side needs RGB triplets (so Tailwind can apply `<alpha-value>`), and
 * hand-converting ~130 hex values is exactly the kind of chore that drifts;
 * so theme.css is generated and should never be edited by hand.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const palette = JSON.parse(readFileSync(join(here, 'palette.json'), 'utf8'));

function triplet(hex) {
  const m = /^#([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) throw new Error(`palette.json: "${hex}" is not a 6-digit hex colour`);
  const n = parseInt(m[1], 16);
  return `${(n >> 16) & 255} ${(n >> 8) & 255} ${n & 255}`;
}

function block(selector, tokens, scheme) {
  const lines = Object.entries(tokens).map(
    ([name, hex]) => `  --${name}: ${triplet(hex)}; /* ${hex} */`,
  );
  return `${selector} {\n  color-scheme: ${scheme};\n${lines.join('\n')}\n}\n`;
}

const darkKeys = Object.keys(palette.dark);
const lightKeys = Object.keys(palette.light);
const missing = darkKeys.filter((k) => !lightKeys.includes(k)).concat(lightKeys.filter((k) => !darkKeys.includes(k)));
if (missing.length) throw new Error(`palette.json: dark/light key mismatch: ${missing.join(', ')}`);

const css = `/* GENERATED FILE — do not edit. Source: src/theme/palette.json.
 * Regenerate with:  npm run theme:css
 *
 * Every colour token is an RGB triplet so Tailwind can compose it as
 * \`rgb(var(--x) / <alpha-value>)\`. Custom properties inherit, so a nested
 * \`data-theme="light"\` container switches its whole subtree (this is how
 * legacy pages keep their light look inside the dark shell).
 */

${block(':root', palette.dark, 'dark')}
${block('[data-theme="dark"]', palette.dark, 'dark')}
${block('[data-theme="light"]', palette.light, 'light')}`;

writeFileSync(join(here, 'theme.css'), css);
console.log(`theme.css written (${darkKeys.length} tokens × 2 themes)`);
