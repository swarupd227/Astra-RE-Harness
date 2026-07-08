import { useEffect, useRef } from 'react';
import { NavLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { clsx } from 'clsx';
import { X } from 'lucide-react';
import { SECTIONS } from '@/shell/LeftNav';
import { api, notificationsApi } from '@/lib/api';

// Mobile navigation drawer. The desktop <LeftNav> is `hidden md:flex`, so below
// the md breakpoint there was previously NO way to reach the nav at all. This
// renders the SAME SECTIONS as an accessible slide-in dialog (md:hidden), so
// primary navigation is reachable on a phone.
export function MobileNav({ open, onClose }: { open: boolean; onClose: () => void }) {
  const panelRef = useRef<HTMLDivElement>(null);
  const restoreRef = useRef<HTMLElement | null>(null);

  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';
  const unread = useQuery({
    queryKey: ['notifications-unread'],
    queryFn: () => notificationsApi.unreadCount(),
  });
  const unreadCount = unread.data?.unread ?? 0;

  useEffect(() => {
    if (!open) return;
    // Remember what to return focus to, then move focus into the drawer.
    restoreRef.current = document.activeElement as HTMLElement | null;
    panelRef.current?.focus();

    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
        return;
      }
      if (e.key === 'Tab' && panelRef.current) {
        // Keep Tab focus inside the drawer while it's open.
        const focusables = panelRef.current.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
        );
        if (focusables.length === 0) return;
        const first = focusables[0];
        const last = focusables[focusables.length - 1];
        if (e.shiftKey && document.activeElement === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      }
    };
    document.addEventListener('keydown', onKey);

    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
      restoreRef.current?.focus();
    };
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 md:hidden" role="dialog" aria-modal="true" aria-label="Navigation">
      <div className="absolute inset-0 bg-slate-900/50" onClick={onClose} aria-hidden="true" />
      <div
        ref={panelRef}
        tabIndex={-1}
        className="absolute inset-y-0 left-0 flex w-72 max-w-[85%] flex-col bg-ace-900 text-slate-200 shadow-2xl outline-none"
      >
        <div className="flex items-center justify-between border-b border-white/10 px-4 py-4">
          <div className="flex items-center gap-2.5">
            <div className="grid h-9 w-9 place-items-center rounded-lg bg-brand-mark font-extrabold text-white">A</div>
            <div className="text-[13px] font-bold leading-tight text-white">Astra Enterprise</div>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close navigation"
            className="flex h-8 w-8 items-center justify-center rounded-md text-slate-300 transition-colors hover:bg-white/10 hover:text-white focus-visible:outline-2 focus-visible:outline-white"
          >
            <X size={18} aria-hidden="true" />
          </button>
        </div>
        <nav className="flex-1 overflow-y-auto px-2.5 py-3" aria-label="Primary">
          {SECTIONS.filter((s) => !s.adminOnly || isAdmin).map((section) => (
            <div key={section.title} className="mb-3">
              <p className="px-2 pb-1 text-[10px] font-semibold uppercase tracking-widest text-slate-500">
                {section.title}
              </p>
              <div className="space-y-0.5">
                {section.items.map((item) => {
                  const Icon = item.icon;
                  const badge = item.to === '/comments' && unreadCount > 0 ? unreadCount : undefined;
                  return (
                    <NavLink
                      key={item.to}
                      to={item.to}
                      end={item.to === '/'}
                      onClick={onClose}
                      className={({ isActive }) =>
                        clsx(
                          'flex items-center gap-2.5 rounded-md px-2.5 py-2 text-[13px] font-medium transition-colors',
                          isActive ? 'bg-white/10 text-white' : 'text-slate-300 hover:bg-white/5 hover:text-white',
                        )
                      }
                    >
                      <Icon size={16} className="shrink-0" aria-hidden="true" />
                      <span className="flex-1 truncate">{item.label}</span>
                      {badge !== undefined && (
                        <span
                          className="rounded-full bg-brand-500 px-1.5 py-0.5 text-[9px] font-bold text-white"
                          aria-label={`${badge} unread`}
                        >
                          {badge > 99 ? '99+' : badge}
                        </span>
                      )}
                    </NavLink>
                  );
                })}
              </div>
            </div>
          ))}
        </nav>
      </div>
    </div>
  );
}
