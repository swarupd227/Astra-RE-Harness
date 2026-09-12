import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { clsx } from 'clsx';
import { ArrowUp, Square } from 'lucide-react';

const MAX_HEIGHT = 220;

/**
 * Sticky bottom composer. ⏎ sends, ⇧⏎ inserts a newline, Esc cancels an
 * in-flight stream. Disabled while a turn is streaming.
 */
export function Composer({
  onSend,
  onCancel,
  streaming,
  disabled = false,
  hint,
  placeholder = 'Ask Astra anything about this programme… (⏎ to send, ⇧⏎ newline)',
  autoFocus = false,
  compact = false,
  prefill = null,
}: {
  onSend: (text: string) => void;
  onCancel: () => void;
  streaming: boolean;
  disabled?: boolean;
  hint?: string;
  placeholder?: string;
  autoFocus?: boolean;
  /** Narrow host (a side panel): no centred max-width, tighter padding. */
  compact?: boolean;
  /**
   * Put text in the box without sending it (a starter the user completes,
   * e.g. "Accept all except …"). Bump `nonce` to apply the same text again.
   */
  prefill?: { text: string; nonce: number } | null;
}) {
  const [text, setText] = useState('');
  const ref = useRef<HTMLTextAreaElement>(null);
  const canSend = !streaming && !disabled && text.trim().length > 0;

  const grow = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, MAX_HEIGHT)}px`;
    el.style.overflowY = el.scrollHeight > MAX_HEIGHT ? 'auto' : 'hidden';
  }, []);

  useEffect(() => {
    grow();
  }, [text, grow]);

  useEffect(() => {
    if (!prefill) return;
    setText(prefill.text);
    window.requestAnimationFrame(() => {
      const el = ref.current;
      if (!el) return;
      el.focus();
      el.setSelectionRange(el.value.length, el.value.length);
    });
  }, [prefill]);

  const submit = useCallback(() => {
    const t = text.trim();
    if (!t || streaming || disabled) return;
    onSend(t);
    setText('');
    window.requestAnimationFrame(() => {
      grow();
      ref.current?.focus();
    });
  }, [text, streaming, disabled, onSend, grow]);

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      submit();
    } else if (e.key === 'Escape' && streaming) {
      e.preventDefault();
      onCancel();
    }
  };

  // The textarea is disabled while streaming, so Esc must be caught globally.
  useEffect(() => {
    if (!streaming) return;
    const h = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        onCancel();
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [streaming, onCancel]);

  // Give focus back once a turn finishes.
  const wasStreaming = useRef(false);
  useEffect(() => {
    if (wasStreaming.current && !streaming && !disabled) ref.current?.focus();
    wasStreaming.current = streaming;
  }, [streaming, disabled]);

  return (
    <div className="shrink-0 border-t border-line-subtle bg-canvas/95 backdrop-blur-sm">
      <form
        className={clsx('w-full', compact ? 'px-4 pb-3 pt-2.5' : 'mx-auto max-w-[880px] px-5 pb-4 pt-3 sm:px-8')}
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
        data-testid="composer"
      >
        <div
          className={clsx(
            'flex items-end gap-2 rounded-2xl border bg-raised px-3 py-2 transition-colors duration-fast',
            'focus-within:border-line-strong focus-within:ring-1 focus-within:ring-volt/60',
            streaming ? 'border-volt/40' : 'border-line',
          )}
        >
          <textarea
            ref={ref}
            data-testid="composer-input"
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder={streaming ? 'Astra is working… (Esc to cancel)' : placeholder}
            disabled={streaming || disabled}
            rows={1}
            autoFocus={autoFocus}
            aria-label="Message Astra"
            className="max-h-[220px] min-h-[24px] w-full resize-none bg-transparent py-1 text-body leading-[1.5] text-ink-primary outline-none placeholder:text-ink-tertiary disabled:cursor-not-allowed disabled:opacity-60"
          />
          {streaming ? (
            <button
              type="button"
              data-testid="composer-cancel"
              onClick={onCancel}
              title="Cancel (Esc)"
              className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-line text-ink-secondary transition-colors hover:border-line-strong hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            >
              <Square size={13} aria-hidden="true" />
              <span className="sr-only">Cancel</span>
            </button>
          ) : (
            <button
              type="submit"
              data-testid="composer-send"
              disabled={!canSend}
              title="Send (⏎)"
              className={clsx(
                'inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-xl transition-all duration-fast',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt focus-visible:ring-offset-2 focus-visible:ring-offset-raised',
                canSend
                  ? 'bg-volt text-on-volt hover:opacity-90'
                  : 'bg-sunken text-ink-tertiary cursor-not-allowed',
              )}
            >
              <ArrowUp size={15} strokeWidth={2.25} aria-hidden="true" />
              <span className="sr-only">Send</span>
            </button>
          )}
        </div>
        <div className="mt-1.5 flex items-center justify-between gap-3 px-1 text-micro text-ink-tertiary">
          <span className="truncate">{hint}</span>
          <span className="hidden shrink-0 sm:inline">
            <kbd className="rounded border border-line-subtle bg-sunken px-1 font-mono">⏎</kbd> send
            {' · '}
            <kbd className="rounded border border-line-subtle bg-sunken px-1 font-mono">⇧⏎</kbd> newline
            {streaming && (
              <>
                {' · '}
                <kbd className="rounded border border-line-subtle bg-sunken px-1 font-mono">esc</kbd> cancel
              </>
            )}
          </span>
        </div>
      </form>
    </div>
  );
}
