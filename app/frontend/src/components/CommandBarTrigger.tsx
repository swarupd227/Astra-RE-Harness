import { Sparkles } from 'lucide-react';
import { useEffect } from 'react';
import { clsx } from 'clsx';
import { openCommandPalette } from '@/copilot/paletteStore';

/**
 * The ⌘K affordance in the top bar. Both the click and the shortcut open
 * the same palette (`src/copilot/CommandPalette`), so the badge printed on
 * the button is never a promise the keyboard doesn't keep.
 */
export function CommandBarTrigger({ className }: { className?: string }) {
  const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      // ⌘K / Ctrl+K opens the palette from anywhere — including from inside
      // the composer, which is where "jump somewhere else" is most wanted.
      if (e.key.toLowerCase() === 'k' && (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        openCommandPalette();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  return (
    <button
      type="button"
      onClick={() => openCommandPalette()}
      className={clsx(
        'hidden h-8 items-center gap-2 rounded-md border border-line-subtle bg-raised/60 px-3 text-caption text-ink-secondary transition-colors duration-fast hover:border-line hover:bg-raised hover:text-ink-primary md:inline-flex',
        className,
      )}
      aria-label="Ask Astra or jump to a view"
      data-testid="command-bar-trigger"
    >
      <Sparkles className="h-3.5 w-3.5 text-volt" aria-hidden="true" />
      <span>Ask Astra or jump to…</span>
      <kbd className="ml-1 rounded border border-line bg-sunken px-1.5 py-0.5 font-mono text-[10px] text-ink-tertiary">
        {isMac ? '⌘' : 'Ctrl'}K
      </kbd>
    </button>
  );
}
