import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react';

/** `matchMedia` as state. SSR-safe (defaults to `false`). */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState<boolean>(() =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia(query).matches
      : false,
  );
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mql = window.matchMedia(query);
    const onChange = () => setMatches(mql.matches);
    onChange();
    mql.addEventListener('change', onChange);
    return () => mql.removeEventListener('change', onChange);
  }, [query]);
  return matches;
}

/** Re-render on an interval — used so relative timestamps stay honest. */
export function useTick(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now;
}

/**
 * Make an element fill the viewport below wherever the shell placed it,
 * regardless of whether the shell scrolls the document or a `<main>`.
 * Returns an inline `height` style: `calc(100dvh - <offsetTop>px)`.
 */
export function useViewportFill<T extends HTMLElement>(ref: RefObject<T>): { height: string } {
  const [top, setTop] = useState(0);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const rect = el.getBoundingClientRect();
      // Add back whatever the nearest scroll parent has already scrolled so
      // the offset is "distance from the top of the scrolling surface".
      let scrolled = 0;
      let p: HTMLElement | null = el.parentElement;
      while (p) {
        const oy = getComputedStyle(p).overflowY;
        if ((oy === 'auto' || oy === 'scroll') && p.scrollHeight > p.clientHeight) {
          scrolled = p.scrollTop;
          break;
        }
        p = p.parentElement;
      }
      if (!p) scrolled = document.scrollingElement?.scrollTop ?? 0;
      setTop(Math.max(0, Math.round(rect.top + scrolled)));
    };
    measure();
    window.addEventListener('resize', measure);
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(measure) : null;
    ro?.observe(document.body);
    return () => {
      window.removeEventListener('resize', measure);
      ro?.disconnect();
    };
  }, [ref]);
  return { height: `calc(100dvh - ${top}px)` };
}

/** Latest value in a ref — for callbacks that must not go stale. */
export function useLatest<T>(value: T): RefObject<T> {
  const ref = useRef(value);
  ref.current = value;
  return ref;
}
