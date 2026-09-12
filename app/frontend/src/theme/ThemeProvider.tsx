import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

export type ThemeName = 'dark' | 'light';

export const THEME_STORAGE_KEY = 'astra.theme';

/** Reads the persisted choice; dark is the product default. */
export function readStoredTheme(): ThemeName {
  try {
    return localStorage.getItem(THEME_STORAGE_KEY) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

/** The theme currently stamped on `<html>` (falls back to the stored choice). */
export function currentTheme(): ThemeName {
  if (typeof document === 'undefined') return 'dark';
  const attr = document.documentElement.getAttribute('data-theme');
  return attr === 'light' ? 'light' : attr === 'dark' ? 'dark' : readStoredTheme();
}

type ThemeContextValue = {
  theme: ThemeName;
  setTheme: (theme: ThemeName) => void;
  toggleTheme: () => void;
};

const ThemeContext = createContext<ThemeContextValue | null>(null);

/**
 * Owns `data-theme` on `<html>` and persists the choice under `astra.theme`.
 * index.html stamps the attribute before first paint (no flash); this keeps
 * it in sync from then on. Nested `data-theme="light"` containers (legacy
 * pages) are independent of this — CSS variables inherit, so they just work.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<ThemeName>(readStoredTheme);

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    try {
      localStorage.setItem(THEME_STORAGE_KEY, theme);
    } catch {
      /* private mode / quota — the in-memory choice still applies */
    }
  }, [theme]);

  const setTheme = useCallback((t: ThemeName) => setThemeState(t), []);
  const toggleTheme = useCallback(() => setThemeState((t) => (t === 'dark' ? 'light' : 'dark')), []);

  const value = useMemo(() => ({ theme, setTheme, toggleTheme }), [theme, setTheme, toggleTheme]);
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

/**
 * Theme state for components. Outside a provider (isolated renders, tests)
 * it degrades to a read-only view of the current `<html>` attribute rather
 * than throwing.
 */
export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext);
  if (ctx) return ctx;
  const theme = currentTheme();
  const setTheme = (t: ThemeName) => document.documentElement.setAttribute('data-theme', t);
  return { theme, setTheme, toggleTheme: () => setTheme(theme === 'dark' ? 'light' : 'dark') };
}
