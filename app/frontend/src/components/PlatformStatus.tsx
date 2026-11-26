import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { clsx } from 'clsx';
import { api } from '@/lib/api';
import { DependencyDot } from './DependencyDot';

const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');

type Sample = { ms: number | null; ok: boolean };

async function probe(): Promise<Sample> {
  const start = performance.now();
  try {
    const ctrl = new AbortController();
    const timeout = setTimeout(() => ctrl.abort(), 3000);
    const res = await fetch(`${API_BASE}/health`, { signal: ctrl.signal });
    clearTimeout(timeout);
    return { ms: Math.round(performance.now() - start), ok: res.ok };
  } catch {
    return { ms: null, ok: false };
  }
}

function providerLabel(name?: string, model?: string): string {
  if (!name) return '…';
  if (name === 'anthropic') return `Claude · ${model ?? ''}`.trim();
  if (name === 'mock') return 'Mock (offline)';
  if (name === 'fail-mock') return 'Mock (chaos)';
  if (name === 'openai_compatible' || name === 'openai-compatible') return 'OpenAI-compatible';
  return model ? `${name} · ${model}` : name;
}

/**
 * One quiet dot in the top bar for "is the platform healthy". The details a user rarely needs — API round-trip,
 * which model is answering, whether the key works — live in its popover, not in the bar itself. The dot turns red
 * when the API is unreachable or the model provider is refusing the key.
 */
export function PlatformStatus() {
  const [sample, setSample] = useState<Sample>({ ms: null, ok: true });
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      const next = await probe();
      if (!cancelled) setSample(next);
    };
    tick();
    const id = setInterval(tick, 15_000);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  }, []);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const provider = useQuery({ queryKey: ['provider-settings'], queryFn: () => api.getProviderSettings() });
  const llm = useQuery({ queryKey: ['llm-settings'], queryFn: api.getLlmSettings, refetchInterval: 60_000 });

  const keyRejected = !!llm.data?.keyRejected;
  const down = !sample.ok;
  const status = down || keyRejected ? 'down' : (sample.ms ?? 0) > 1500 ? 'pending' : 'ok';
  const summary = down
    ? 'API unreachable'
    : keyRejected
      ? 'Model key rejected'
      : 'All systems normal';

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls="platform-status-panel"
        aria-label={`Platform status: ${summary}`}
        title={summary}
        className={clsx(
          'flex h-8 items-center gap-2 rounded-md px-2 text-caption transition-colors duration-fast hover:bg-raised',
          status === 'down' ? 'text-status-failed' : 'text-ink-secondary hover:text-ink-primary',
        )}
        data-testid="topbar-status"
      >
        <DependencyDot status={status} />
        {status === 'down' && <span className="hidden font-medium sm:inline">{summary}</span>}
      </button>

      {open && (
        <div
          id="platform-status-panel"
          role="dialog"
          aria-label="Platform status"
          className="absolute right-0 top-10 z-40 w-72 rounded-lg border border-line bg-raised p-3 text-caption shadow-e2"
          data-testid="topbar-status-panel"
        >
          <p className="mb-2 font-semibold text-ink-primary">{summary}</p>
          <dl className="space-y-1.5">
            <div className="flex items-center justify-between gap-3">
              <dt className="text-ink-secondary">API</dt>
              <dd className={clsx('font-mono', down ? 'text-status-failed' : 'text-ink-primary')}>
                {down ? 'unreachable' : sample.ms != null ? `${sample.ms} ms` : '…'}
              </dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-ink-secondary">Model</dt>
              <dd className="truncate font-mono text-ink-primary" data-testid="topbar-provider-pill">
                {providerLabel(provider.data?.provider?.name, provider.data?.provider?.model)}
              </dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-ink-secondary">API key</dt>
              <dd className={clsx('font-mono', keyRejected ? 'text-status-failed' : 'text-ink-primary')}>
                {keyRejected ? 'rejected' : llm.data?.keyConfigured ? 'working' : llm.data ? 'not set' : '…'}
              </dd>
            </div>
          </dl>
          <div className="mt-3 flex gap-3 border-t border-line-subtle pt-2">
            <Link to="/system" onClick={() => setOpen(false)} className="text-ink-link hover:underline">
              System health
            </Link>
            <Link to="/platform/llm" onClick={() => setOpen(false)} className="text-ink-link hover:underline">
              Model settings
            </Link>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * A full-width warning under the top bar while the model provider refuses the API key — otherwise every extract,
 * doc and code run fails one by one with a 401 buried in its log.
 */
export function LlmKeyBanner() {
  const llm = useQuery({ queryKey: ['llm-settings'], queryFn: api.getLlmSettings, refetchInterval: 60_000 });
  if (!llm.data?.keyRejected) return null;
  return (
    <div
      role="alert"
      className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-status-failed/40 bg-status-fail/10 px-4 py-2 text-caption text-ink-primary"
      data-testid="llm-key-banner"
    >
      <span className="font-semibold text-status-failed">Claude is rejecting the API key.</span>
      <span className="text-ink-secondary">
        Extraction, documents and code generation will fail until an Admin saves a working key.
      </span>
      <Link to="/platform/llm" className="font-medium text-ink-link hover:underline">
        Open model settings
      </Link>
    </div>
  );
}
