import { useEffect, useRef } from 'react';
import { NavLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion';
import { X } from 'lucide-react';
import { LogoLockup } from '@/components/Logo';
import { AskAstraLink, isExactNavRoute, navItemClass, navTestId, SECTIONS } from '@/shell/LeftNav';
import { RailProgrammes } from '@/shell/RailProgrammes';
import { api, notificationsApi } from '@/lib/api';

/**
 * Mobile navigation drawer. The desktop rail is `hidden md:flex`, so below
 * the md breakpoint this is the only way to reach navigation. Renders the
 * SAME sections as an accessible slide-in dialog (md:hidden).
 */
export function MobileNav({ open, onClose }: { open: boolean; onClose: () => void }) {
  const panelRef = useRef<HTMLDivElement>(null);
  const restoreRef = useRef<HTMLElement | null>(null);
  const reduceMotion = useReducedMotion();

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

  const duration = reduceMotion ? 0 : 0.2;

  return (
    <AnimatePresence>
      {open && (
        <motion.div
          className="fixed inset-0 z-50 md:hidden"
          role="dialog"
          aria-modal="true"
          aria-label="Navigation"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: reduceMotion ? 0 : 0.15 }}
        >
          <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={onClose} aria-hidden="true" />
          <motion.div
            ref={panelRef}
            tabIndex={-1}
            className="absolute inset-y-0 left-0 flex w-72 max-w-[85%] flex-col border-r border-line-subtle bg-sunken text-ink-primary shadow-e3 outline-none"
            initial={{ x: reduceMotion ? 0 : -32, opacity: 0 }}
            animate={{ x: 0, opacity: 1 }}
            exit={{ x: reduceMotion ? 0 : -32, opacity: 0 }}
            transition={{ type: 'tween', duration, ease: 'easeOut' }}
          >
            <div className="flex h-14 items-center justify-between border-b border-line-subtle px-4">
              <LogoLockup size="sm" />
              <button
                type="button"
                onClick={onClose}
                aria-label="Close navigation"
                className="flex h-8 w-8 items-center justify-center rounded-md text-ink-secondary transition-colors duration-fast hover:bg-raised hover:text-ink-primary"
              >
                <X size={18} aria-hidden="true" />
              </button>
            </div>
            <nav className="flex-1 overflow-y-auto px-2.5 py-3" aria-label="Primary">
              <div className="mb-4">
                <AskAstraLink collapsed={false} onNavigate={onClose} />
              </div>
              <div className="mb-4">
                <p className="label mb-1 px-2.5">Programmes</p>
                <RailProgrammes onNavigate={onClose} />
              </div>
              {SECTIONS.filter((s) => !s.adminOnly || isAdmin).map((section) => (
                <div key={section.title} className="mb-4">
                  <p className="label mb-1 px-2.5">{section.title}</p>
                  <div className="space-y-0.5">
                    {section.items.map((item) => {
                      const Icon = item.icon;
                      const badge = item.to === '/comments' && unreadCount > 0 ? unreadCount : undefined;
                      return (
                        <NavLink
                          key={item.to}
                          to={item.to}
                          end={isExactNavRoute(item.to)}
                          onClick={onClose}
                          className={navItemClass(false)}
                          data-testid={`mobile-${navTestId(item.label)}`}
                        >
                          <Icon size={16} className="shrink-0" aria-hidden="true" />
                          <span className="flex-1 truncate">{item.label}</span>
                          {badge !== undefined && (
                            <span
                              className="rounded-full bg-volt px-1.5 py-0.5 text-[9px] font-bold text-on-volt"
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
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
