import { clsx } from 'clsx';

/**
 * Astra monogram glyph. Octagon outline with an angular "A" cut into it,
 * intended to read like an instrument bezel — not a SaaS logo.
 *
 * Renders at 32x32 by default; scales via the `size` prop.
 */
export function LogoGlyph({
  size = 32,
  className,
  title = 'Astra RE Harness',
}: {
  size?: number;
  className?: string;
  title?: string;
}) {
  return (
    <svg
      role="img"
      aria-label={title}
      width={size}
      height={size}
      viewBox="0 0 32 32"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      className={clsx('shrink-0', className)}
    >
      <title>{title}</title>
      {/* Octagon bezel */}
      <path
        d="M10 2 L22 2 L30 10 L30 22 L22 30 L10 30 L2 22 L2 10 Z"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinejoin="round"
      />
      {/* Inner ring fragment — circuit cut */}
      <path
        d="M24.5 8 L27 10.5"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
      <path
        d="M5 21.5 L7.5 24"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
      {/* The A */}
      <path
        d="M10.5 23 L16 9.5 L21.5 23"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinejoin="round"
        strokeLinecap="round"
      />
      <path
        d="M12.7 18.5 L19.3 18.5"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
      />
      {/* Accent dot — instrument LED */}
      <circle cx="16" cy="6" r="1.2" fill="#E5732C" />
    </svg>
  );
}

/**
 * Logo lockup: glyph + wordmark. Used in the TopBar and LeftNav footer.
 */
export function LogoLockup({
  size = 'md',
  className,
}: {
  size?: 'sm' | 'md';
  className?: string;
}) {
  const dims = size === 'sm' ? { glyph: 22, type: 'text-body' } : { glyph: 26, type: 'text-h-md' };
  return (
    <span className={clsx('inline-flex items-center gap-2', className)}>
      <LogoGlyph size={dims.glyph} className="text-ink-primary" />
      <span className={clsx(dims.type, 'font-semibold tracking-tight text-ink-primary')}>
        Astra <span className="text-ink-secondary font-normal">RE Harness</span>
      </span>
    </span>
  );
}
