import { forwardRef } from 'react';
import { clsx } from 'clsx';

type Variant = 'primary' | 'secondary' | 'ghost' | 'destructive';
type Size = 'sm' | 'md' | 'lg';

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  loading?: boolean;
}

const base =
  'inline-flex items-center justify-center gap-2 rounded-lg font-semibold transition duration-fast focus-visible:outline-2 focus-visible:outline-volt disabled:opacity-50 disabled:cursor-not-allowed';

// Design system v2:
//   primary     → volt (the one accent; reserved for the primary CTA)
//   secondary   → raised surface with a line border (default action)
//   ghost       → transparent, hover background only
//   destructive → status-fail for irreversible actions
// Every colour is a theme token, so the same button reads correctly in the
// dark shell and inside the light legacy wrapper.
const variants: Record<Variant, string> = {
  primary: 'bg-volt text-on-volt hover:brightness-95 active:brightness-90',
  secondary: 'bg-raised text-ink-primary border border-line hover:bg-sunken',
  ghost: 'text-ink-secondary hover:bg-sunken hover:text-ink-primary',
  destructive: 'bg-status-fail text-ink-inverse hover:brightness-95 active:brightness-90',
};

const sizes: Record<Size, string> = {
  sm: 'h-8 px-3 text-body',
  md: 'h-10 px-4 text-body',
  lg: 'h-12 px-5 text-body-lg',
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', loading, className, children, disabled, ...rest },
  ref,
) {
  return (
    <button
      ref={ref}
      className={clsx(base, variants[variant], sizes[size], className)}
      disabled={disabled || loading}
      {...rest}
    >
      {loading && (
        <span
          className="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent"
          aria-hidden="true"
        />
      )}
      {children}
    </button>
  );
});
