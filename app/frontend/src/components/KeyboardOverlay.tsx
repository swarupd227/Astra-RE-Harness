import { useEffect } from 'react';
import { X } from 'lucide-react';

// `available: false` shortcuts are listed but not wired up to any handler
// yet — verified against the actual keydown listeners in App.tsx and
// SpecReviewPage.tsx, not guessed. Keeping them documented (as a roadmap of
// what a claim card is meant to support) is fine; showing them identically
// to working shortcuts is not — a user pressing 'a' expecting to accept a
// claim and having nothing happen reads as broken, not "not built yet".
type Shortcut = { keys: string[]; label: string; available?: boolean };
type Section = { title: string; items: Shortcut[] };

const SECTIONS: Section[] = [
  {
    title: 'Global',
    items: [
      { keys: ['?'], label: 'Show this keyboard help' },
      { keys: ['⌘', 'K'], label: 'Open command bar' },
      { keys: ['Esc'], label: 'Close any modal or overlay' },
    ],
  },
  {
    title: 'Navigation',
    items: [
      { keys: ['g', 'h'], label: 'Go to home' },
      { keys: ['g', 's'], label: 'Go to system status' },
    ],
  },
  {
    title: 'Spec review',
    items: [
      { keys: ['j'], label: 'Next claim' },
      { keys: ['k'], label: 'Previous claim' },
      { keys: ['a'], label: 'Accept claim', available: false },
      { keys: ['e'], label: 'Edit claim', available: false },
      { keys: ['r'], label: 'Reject claim (requires reason)', available: false },
      // Global '?' always wins (it's a window-level listener with no scoping
      // to Spec Review), so this one can never actually fire — not a "?"
      // collision to resolve, just a shortcut that was never given a
      // handler or a non-conflicting key.
      { keys: ['?'], label: 'Open question on claim', available: false },
      { keys: ['s'], label: 'Open sign-off modal (when ready)' },
    ],
  },
];

export function KeyboardOverlay({ open, onClose }: { open: boolean; onClose: () => void }) {
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center motion-safe:animate-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby="keyboard-help-title"
      onClick={onClose}
    >
      {/* scrim */}
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      {/* panel */}
      <div
        className="relative max-h-[85vh] w-[640px] max-w-[92vw] overflow-y-auto rounded-lg border border-border-subtle bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between border-b border-border-subtle px-6 py-4">
          <div>
            <h2 id="keyboard-help-title" className="text-h-md font-semibold text-ink-primary">
              Keyboard shortcuts
            </h2>
            <p className="mt-1 text-caption text-ink-secondary">
              Greyed-out shortcuts are listed but not wired up yet.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1.5 text-ink-secondary transition-colors duration-fast hover:bg-sunken hover:text-ink-primary"
            aria-label="Close keyboard help"
          >
            <X className="h-4 w-4" aria-hidden="true" />
          </button>
        </header>

        <div className="px-6 py-4 space-y-6">
          {SECTIONS.map((section) => (
            <section key={section.title}>
              <h3 className="mb-2 text-caption font-medium uppercase tracking-wider text-ink-tertiary">
                {section.title}
              </h3>
              <ul className="divide-y divide-border-subtle">
                {section.items.map((item) => {
                  const available = item.available ?? true;
                  return (
                    <li
                      key={item.label}
                      className={
                        'flex items-center justify-between py-2 text-body' +
                        (available ? '' : ' opacity-45')
                      }
                    >
                      <span className="text-ink-primary">{item.label}</span>
                      <span className="flex items-center gap-1">
                        {item.keys.map((k, i) => (
                          <kbd
                            key={i}
                            className={
                              'rounded border px-1.5 py-0.5 font-mono text-caption shadow-e1 ' +
                              (available
                                ? 'border-border bg-canvas text-ink-primary'
                                : 'border-border-subtle bg-canvas text-ink-tertiary')
                            }
                          >
                            {k}
                          </kbd>
                        ))}
                      </span>
                    </li>
                  );
                })}
              </ul>
            </section>
          ))}
        </div>
      </div>
    </div>
  );
}
