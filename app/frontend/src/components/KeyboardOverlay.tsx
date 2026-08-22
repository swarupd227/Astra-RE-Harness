import { useEffect } from 'react';
import { X } from 'lucide-react';

type Shortcut = { keys: string[]; label: string };
type Section = { title: string; items: Shortcut[] };

const SECTIONS: Section[] = [
  {
    title: 'Global',
    items: [
      { keys: ['?'], label: 'Show this keyboard help' },
      { keys: ['⌘', 'K'], label: 'Open command bar (Phase C)' },
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
    title: 'Spec review (Phase B)',
    items: [
      { keys: ['j'], label: 'Next claim' },
      { keys: ['k'], label: 'Previous claim' },
      { keys: ['a'], label: 'Accept claim' },
      { keys: ['e'], label: 'Edit claim' },
      { keys: ['r'], label: 'Reject claim (requires reason)' },
      { keys: ['?'], label: 'Open question on claim' },
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
              Greyed shortcuts unlock as their phase ships.
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
                {section.items.map((item) => (
                  <li key={item.label} className="flex items-center justify-between py-2 text-body">
                    <span className="text-ink-primary">{item.label}</span>
                    <span className="flex items-center gap-1">
                      {item.keys.map((k, i) => (
                        <kbd
                          key={i}
                          className="rounded border border-border bg-canvas px-1.5 py-0.5 font-mono text-caption text-ink-primary shadow-e1"
                        >
                          {k}
                        </kbd>
                      ))}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      </div>
    </div>
  );
}
