import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { EvidenceDrawer, type EvidenceRequest } from '@/evidence/EvidenceDrawer';

type EvidenceApi = {
  /** Opens the evidence panel. Several ids open the stacked view. */
  open: (request: EvidenceRequest) => void;
  close: () => void;
};

const Ctx = createContext<EvidenceApi | null>(null);

/**
 * Owns the one evidence panel for the whole app, so a citation chip anywhere — on a page, in a
 * card in the conversation — opens the same panel. Mounted once, around the shell.
 *
 * A request belongs to the page it was opened on: it is remembered with that page's path and only
 * shown while the app is still there. (Closing it from an effect on navigation would also close a
 * panel that the next page opens as it mounts.)
 */
export function EvidenceProvider({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const [opened, setOpened] = useState<{ request: EvidenceRequest; at: string } | null>(null);
  const opener = useRef<HTMLElement | null>(null);
  const here = useRef(pathname);
  here.current = pathname;

  const open = useCallback((request: EvidenceRequest) => {
    opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setOpened({ request, at: here.current });
  }, []);

  const close = useCallback(() => {
    setOpened(null);
    // Give focus back to what opened the panel, if it is still on the page.
    const el = opener.current;
    opener.current = null;
    if (el?.isConnected) el.focus();
  }, []);

  const api = useMemo(() => ({ open, close }), [open, close]);
  return (
    <Ctx.Provider value={api}>
      {children}
      {opened && opened.at === pathname && <EvidenceDrawer request={opened.request} onClose={close} />}
    </Ctx.Provider>
  );
}

export function useEvidence(): EvidenceApi {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error('useEvidence must be used inside <EvidenceProvider>');
  return ctx;
}
