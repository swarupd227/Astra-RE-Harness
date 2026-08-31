import { Search } from 'lucide-react';
import { useEffect, useState } from 'react';

/**
 * Phase A: a placeholder that announces the surface and explains when
 * it ships. Phase C wires this to a real kbar-style command bar.
 */
export function CommandBarTrigger() {
  const [open, setOpen] = useState(false);
  const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform);

  // The button has always advertised a ⌘K / Ctrl+K badge, but nothing in the
  // app ever listened for it — pressing the exact shortcut printed on screen
  // did nothing, not even the "not available yet" modal the button itself
  // opens correctly on click. Wire the same trigger to the keyboard so the
  // two paths behave identically, rather than removing the badge (the
  // surface is real, if unimplemented — see the modal copy below).
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        setOpen(true);
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  // The modal's own copy promises "Press Esc to close", but nothing ever
  // listened for Escape — only clicking the backdrop or the button worked.
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open]);

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="hidden items-center gap-2 rounded-md border border-border-subtle bg-canvas px-3 py-1.5 text-body text-ink-secondary transition-colors duration-fast hover:bg-sunken hover:text-ink-primary md:inline-flex"
      >
        <Search className="h-4 w-4" aria-hidden="true" />
        <span>Search projects, routines…</span>
        <kbd className="ml-2 rounded border border-border bg-raised px-1.5 py-0.5 font-mono text-[10px] text-ink-tertiary">
          {isMac ? '⌘' : 'Ctrl'}K
        </kbd>
      </button>

      {open && (
        <div
          className="fixed inset-0 z-50 flex items-start justify-center pt-[20vh] motion-safe:animate-fade-in"
          role="dialog"
          aria-modal="true"
          onClick={() => setOpen(false)}
        >
          <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
          <div
            className="relative w-[560px] max-w-[92vw] rounded-lg border border-border-subtle bg-raised p-6 shadow-e3"
            onClick={(e) => e.stopPropagation()}
          >
            <h2 className="text-h-md font-semibold text-ink-primary">Command bar</h2>
            <p className="mt-2 text-body text-ink-secondary">
              Search across projects and jump between screens. Not available yet.
            </p>
            <div className="mt-4 flex items-center gap-2 rounded-md border border-border-subtle bg-canvas px-3 py-2 font-mono text-caption text-ink-tertiary">
              <Search className="h-3.5 w-3.5" aria-hidden="true" />
              Not available yet
            </div>
            <button
              type="button"
              onClick={() => setOpen(false)}
              className="mt-4 text-caption text-ink-tertiary hover:text-ink-primary"
            >
              Press Esc to close.
            </button>
          </div>
        </div>
      )}
    </>
  );
}
