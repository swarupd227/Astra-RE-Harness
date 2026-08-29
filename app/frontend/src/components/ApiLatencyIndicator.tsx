import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { clsx } from 'clsx';
import { DependencyDot } from './DependencyDot';
import { Tooltip } from './Tooltip';

const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');

type Sample = { ms: number | null; ok: boolean };

async function probe(): Promise<Sample> {
  const start = performance.now();
  try {
    const ctrl = new AbortController();
    const timeout = setTimeout(() => ctrl.abort(), 3000);
    const res = await fetch(`${API_BASE}/health`, { signal: ctrl.signal });
    clearTimeout(timeout);
    const ms = Math.round(performance.now() - start);
    return { ms, ok: res.ok };
  } catch {
    return { ms: null, ok: false };
  }
}

export function ApiLatencyIndicator() {
  // ok starts true (not false): this mounts fresh on every full navigation,
  // and a false default rendered "unreachable" — the actual down state —
  // for up to 3s before the first probe() resolves, on every single page
  // load, regardless of whether the API was ever actually unreachable.
  const [sample, setSample] = useState<Sample>({ ms: null, ok: true });
  const [inFlight, setInFlight] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      setInFlight(true);
      const next = await probe();
      if (cancelled) return;
      setSample(next);
      setInFlight(false);
    };
    tick();
    const id = setInterval(tick, 10_000);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  }, []);

  const status = !sample.ok ? 'down' : (sample.ms ?? 9999) > 1500 ? 'pending' : 'ok';
  const label = !sample.ok
    ? 'unreachable'
    : sample.ms !== null
      ? `${sample.ms} ms`
      : '—';
  const tooltip = !sample.ok
    ? 'API is not reachable. Click for system status.'
    : `Round-trip to /health: ${label}. Click for system status.`;

  return (
    <Tooltip content={tooltip}>
      <Link
        to="/system"
        className={clsx(
          'inline-flex items-center gap-2 rounded-md border border-border-subtle bg-canvas px-2.5 py-1.5 font-mono text-caption transition-colors duration-fast hover:bg-sunken',
          !sample.ok ? 'text-status-failed' : 'text-ink-secondary hover:text-ink-primary',
        )}
        aria-label={`API ${label}`}
      >
        <DependencyDot status={status} pulse={inFlight} />
        <span>{label}</span>
      </Link>
    </Tooltip>
  );
}
