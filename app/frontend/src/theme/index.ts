/**
 * Design tokens for code that can't use Tailwind classes — Monaco themes,
 * cytoscape stylesheets, recharts fills, canvas drawing.
 *
 *   themeHex('volt')                 → hex for the theme currently on <html>
 *   themeHex('status-ok', 'light')   → hex for a specific theme
 *   themeHex.dark / themeHex.light   → the full typed tables
 *
 * Tailwind classes remain the default; reach for this only when you need a
 * raw value. Source of truth: ./palette.json (theme.css is generated from it).
 */
import palette from './palette.json';
import { currentTheme, type ThemeName } from './ThemeProvider';

export type { ThemeName } from './ThemeProvider';
export { ThemeProvider, useTheme, currentTheme, readStoredTheme, THEME_STORAGE_KEY } from './ThemeProvider';

export type TokenName = keyof typeof palette.dark;
export type ThemeTable = Readonly<Record<TokenName, string>>;

const tables: Readonly<Record<ThemeName, ThemeTable>> = {
  dark: palette.dark,
  light: palette.light,
};

function lookup(name: TokenName, theme?: ThemeName): string {
  return tables[theme ?? currentTheme()][name];
}

/** Hex lookup by token name, with the per-theme tables attached. */
export const themeHex: typeof lookup & { readonly dark: ThemeTable; readonly light: ThemeTable } = Object.assign(
  lookup,
  { dark: tables.dark, light: tables.light },
);

/** Every token name, in palette order — handy for docs/debug swatches. */
export const TOKEN_NAMES = Object.keys(palette.dark) as TokenName[];
