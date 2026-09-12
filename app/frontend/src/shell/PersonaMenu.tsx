import { ChevronDown, Check, ShieldCheck, ClipboardCheck, Eye, Settings2 } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion';
import { clsx } from 'clsx';
import { ALL_PERSONAS, type Persona } from '@/lib/persona';
import { getPersona, setPersona } from '@/lib/api';

const META: Record<Persona, { label: string; tagline: string; icon: typeof ShieldCheck; tone: string }> = {
  engineer: {
    label: 'Engineer',
    tagline: 'Operates the pipeline. Triggers ingest, parse, extract, scaffold.',
    icon: ShieldCheck,
    tone: 'text-persona-engineer',
  },
  sme: {
    label: 'SME',
    tagline: 'Reviews and signs specifications.',
    icon: ClipboardCheck,
    tone: 'text-persona-sme',
  },
  observer: {
    label: 'Observer',
    tagline: 'Read-only oversight. Audits and exports artifacts.',
    icon: Eye,
    tone: 'text-persona-observer',
  },
  admin: {
    label: 'Admin',
    tagline: 'Configures providers, credentials, prompt routing, cost caps.',
    icon: Settings2,
    tone: 'text-persona-admin',
  },
};

/**
 * Dev persona switcher. The trigger is the ONLY `aria-haspopup="menu"`
 * button in the shell — the e2e suite locates it by exactly that — and the
 * items keep `role="menuitemradio"` with an accessible name of
 * "<Label> <tagline>".
 */
export function PersonaMenu() {
  const [persona, setPersonaState] = useState<Persona>(getPersona());
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const reduceMotion = useReducedMotion();

  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    window.addEventListener('mousedown', onClick);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onClick);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const onPick = (p: Persona) => {
    setPersona(p);
    setPersonaState(p);
    setOpen(false);
    // Reload so cached queries (whoami, readiness) re-run with the new header.
    window.location.reload();
  };

  const Icon = META[persona].icon;

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex h-8 items-center gap-2 rounded-md border border-line-subtle bg-raised/60 px-2 text-caption transition-colors duration-fast hover:border-line hover:bg-raised"
        aria-haspopup="menu"
        aria-expanded={open}
      >
        <span className={clsx('flex h-5 w-5 items-center justify-center rounded-full bg-sunken', META[persona].tone)}>
          <Icon className="h-3 w-3" aria-hidden="true" />
        </span>
        <span className="hidden font-medium text-ink-primary sm:inline">{META[persona].label}</span>
        <ChevronDown className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
      </button>
      <AnimatePresence>
        {open && (
          <motion.div
            role="menu"
            className="absolute right-0 z-40 mt-2 w-80 origin-top-right overflow-hidden rounded-lg border border-line bg-raised text-ink-primary shadow-e3"
            initial={{ opacity: 0, y: reduceMotion ? 0 : -4, scale: reduceMotion ? 1 : 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: reduceMotion ? 0 : -4, scale: reduceMotion ? 1 : 0.98 }}
            transition={{ duration: reduceMotion ? 0 : 0.14, ease: 'easeOut' }}
          >
            <header className="border-b border-line-subtle px-4 py-3">
              <p className="text-caption text-ink-tertiary">Switch persona (dev-only · auth deferred)</p>
              <p className="mt-1 font-mono text-caption text-ink-secondary">
                X-Dev-Persona header is set on every request.
              </p>
            </header>
            <ul>
              {ALL_PERSONAS.map((p) => {
                const m = META[p];
                const PIcon = m.icon;
                const active = p === persona;
                return (
                  <li key={p}>
                    <button
                      type="button"
                      role="menuitemradio"
                      aria-checked={active}
                      onClick={() => onPick(p)}
                      className={clsx(
                        'flex w-full items-start gap-3 px-4 py-3 text-left transition-colors duration-fast',
                        active ? 'bg-sunken' : 'hover:bg-sunken/60',
                      )}
                    >
                      <span className={clsx('mt-0.5 flex h-7 w-7 items-center justify-center rounded-full bg-sunken ring-1 ring-line-subtle', m.tone)}>
                        <PIcon className="h-4 w-4" aria-hidden="true" />
                      </span>
                      <span className="flex-1">
                        <span className="flex items-center gap-2">
                          <span className="text-body font-semibold text-ink-primary">{m.label}</span>
                          {active && <Check className="h-3.5 w-3.5 text-status-ok" aria-hidden="true" />}
                        </span>
                        <span className="mt-0.5 block text-caption text-ink-secondary">{m.tagline}</span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
