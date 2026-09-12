/**
 * Theme-following colours for recharts / cytoscape / canvas code, which
 * can't use Tailwind classes. Everything resolves through `themeHex()` so a
 * chart repaints when the toggle flips (callers re-render on `theme`).
 */
import { createElement, type CSSProperties, type ReactNode } from 'react';
import { themeHex, type ThemeName } from './index';
import { useTheme } from './ThemeProvider';

export type ChartTheme = {
  theme: ThemeName;
  /** Grid lines / axis lines. */
  grid: string;
  /** Axis tick labels. */
  tick: string;
  /** Stroke that separates pie slices from each other (the card surface). */
  surface: string;
  /** Neutral "pending / not started" series. */
  neutral: string;
  /** Lifecycle-state series colours, one per funnel stage. */
  state: { parsed: string; draft: string; signed: string; scaffolded: string; committed: string };
  ok: string;
  warn: string;
  fail: string;
  info: string;
  volt: string;
  /** Wave 1…5 ramp (volt → sand). */
  wave: Record<1 | 2 | 3 | 4 | 5, string>;
  /** `contentStyle` for a recharts <Tooltip>. */
  tooltip: CSSProperties;
  /** `wrapperStyle` for a recharts <Legend>. */
  legend: CSSProperties;
  /** `formatter` for a recharts <Legend> — labels in secondary ink instead of the series colour. */
  legendText: (value: ReactNode) => ReactNode;
};

export function chartTheme(theme: ThemeName): ChartTheme {
  const t = (name: Parameters<typeof themeHex>[0]) => themeHex(name, theme);
  return {
    theme,
    grid: t('line-subtle'),
    tick: t('ink-tertiary'),
    surface: t('raised'),
    neutral: t('line'),
    state: {
      parsed: t('line-strong'),
      draft: t('status-warn'),
      signed: t('status-info'),
      scaffolded: t('wave-2'),
      committed: t('status-ok'),
    },
    ok: t('status-ok'),
    warn: t('status-warn'),
    fail: t('status-fail'),
    info: t('status-info'),
    volt: t('volt'),
    wave: { 1: t('wave-1'), 2: t('wave-2'), 3: t('wave-3'), 4: t('wave-4'), 5: t('wave-5') },
    tooltip: {
      background: t('raised'),
      color: t('ink-primary'),
      border: `1px solid ${t('line')}`,
      borderRadius: 8,
      fontSize: 12,
      boxShadow: '0 4px 12px rgba(0,0,0,.28)',
    },
    legend: { fontSize: 11, paddingTop: 8, color: t('ink-secondary') },
    legendText: (value) => createElement('span', { style: { color: t('ink-secondary') } }, value),
  };
}

/** Hook form — re-renders the chart when the theme toggles. */
export function useChartTheme(): ChartTheme {
  const { theme } = useTheme();
  return chartTheme(theme);
}
