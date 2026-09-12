import type { ReactNode } from 'react';

/**
 * Wraps a pre-v2 page in a light-theme container. Every colour token is a
 * CSS variable that inherits, so `data-theme="light"` here re-points the
 * whole subtree at the light palette — the page's hard-coded `bg-white` /
 * `text-slate-*` keep making sense until Increment 2 restyles it. The dark
 * shell (top bar, rail) stays dark around it.
 */
export function LegacyView({ children }: { children: ReactNode }) {
  return (
    <div data-theme="light" className="legacy-surface" data-testid="legacy-view">
      {children}
    </div>
  );
}
