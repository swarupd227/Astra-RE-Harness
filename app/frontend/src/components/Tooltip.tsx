import { clsx } from 'clsx';
import { useId, useState, type ReactNode } from 'react';

/**
 * Tiny CSS-only tooltip. Good enough for Phase A; replaced with Radix
 * (or shadcn/ui's TooltipProvider) once we want positioning + portals.
 */
export function Tooltip({
  content,
  children,
  side = 'bottom',
  className,
}: {
  content: ReactNode;
  children: ReactNode;
  side?: 'top' | 'bottom' | 'right';
  className?: string;
}) {
  const id = useId();
  const [open, setOpen] = useState(false);

  return (
    <span
      className={clsx('relative inline-flex', className)}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)}
      onBlur={() => setOpen(false)}
    >
      <span aria-describedby={open ? id : undefined}>{children}</span>
      {open && (
        <span
          id={id}
          role="tooltip"
          className={clsx(
            'pointer-events-none absolute z-50 whitespace-nowrap rounded-md border border-border-subtle bg-ink-primary px-2 py-1 text-caption text-ink-inverse shadow-e2 motion-safe:animate-fade-in',
            side === 'bottom' && 'left-1/2 top-full mt-1.5 -translate-x-1/2',
            side === 'top' && 'left-1/2 bottom-full mb-1.5 -translate-x-1/2',
            side === 'right' && 'left-full top-1/2 ml-1.5 -translate-y-1/2',
          )}
        >
          {content}
        </span>
      )}
    </span>
  );
}
