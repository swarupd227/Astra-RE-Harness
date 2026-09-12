import { AnimatePresence, motion, useReducedMotion } from 'framer-motion';
import { ArrowUpRight, PanelRightClose, X } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { Artifact } from '@/lib/conversations';
import { artifactIcon, artifactKindLabel, artifactLink, artifactTitle } from './artifacts/meta';
import { renderArtifact } from './artifacts/registry';

/**
 * The right pane: the selected artifact, large. Docked beside the thread on
 * `xl:`; a slide-over below that.
 */
export function ArtifactPane({
  artifact,
  mode,
  open = true,
  onClose,
  onIntent,
}: {
  artifact: Artifact | null;
  mode: 'docked' | 'overlay';
  open?: boolean;
  onClose: () => void;
  /** Lets cards in the pane send an intent to the thread ("Why?", "Resume"). */
  onIntent?: (intent: string) => void;
}) {
  const reduced = useReducedMotion();

  if (mode === 'docked') {
    return (
      <aside
        data-testid="artifact-pane"
        data-mode="docked"
        className="hidden w-[440px] shrink-0 flex-col border-l border-line-subtle bg-canvas xl:flex"
        aria-label="Artifact"
      >
        <PaneBody
          artifact={artifact}
          onClose={onClose}
          onIntent={onIntent}
          closeIcon={<PanelRightClose size={16} aria-hidden="true" />}
        />
      </aside>
    );
  }

  return (
    <AnimatePresence>
      {open && (
        <div className="fixed inset-0 z-40 xl:hidden" role="dialog" aria-modal="true" aria-label="Artifact">
          <motion.div
            className="absolute inset-0 bg-black/60"
            initial={reduced ? false : { opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.16 }}
            onClick={onClose}
          />
          <motion.aside
            data-testid="artifact-pane"
            data-mode="overlay"
            className="absolute inset-y-0 right-0 flex w-full max-w-[480px] flex-col border-l border-line bg-canvas shadow-e3"
            initial={reduced ? false : { x: 32, opacity: 0 }}
            animate={{ x: 0, opacity: 1 }}
            exit={reduced ? { opacity: 0 } : { x: 32, opacity: 0 }}
            transition={{ duration: 0.2, ease: 'easeOut' }}
          >
            <PaneBody
              artifact={artifact}
              onClose={onClose}
              onIntent={onIntent}
              closeIcon={<X size={16} aria-hidden="true" />}
            />
          </motion.aside>
        </div>
      )}
    </AnimatePresence>
  );
}

function PaneBody({
  artifact,
  onClose,
  onIntent,
  closeIcon,
}: {
  artifact: Artifact | null;
  onClose: () => void;
  onIntent?: (intent: string) => void;
  closeIcon: React.ReactNode;
}) {
  const Icon = artifact ? artifactIcon(artifact.kind) : null;
  const link = artifact ? artifactLink(artifact) : null;
  return (
    <>
      <header className="flex h-12 shrink-0 items-center gap-2 border-b border-line-subtle px-4">
        {Icon && <Icon size={15} className="shrink-0 text-ink-tertiary" aria-hidden="true" />}
        <div className="min-w-0 flex-1">
          <div className="truncate text-caption font-medium text-ink-primary">
            {artifact ? artifactTitle(artifact) : 'Artifact'}
          </div>
          {artifact && (
            <div className="truncate text-micro uppercase tracking-wide text-ink-tertiary">
              {artifactKindLabel(artifact.kind)}
            </div>
          )}
        </div>
        {link && (
          <Link
            to={link.href}
            data-testid="artifact-pane-open"
            className="inline-flex shrink-0 items-center gap-1 rounded-md border border-line-subtle px-2 py-1 text-micro text-ink-secondary transition-colors hover:border-line hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          >
            Open full view
            <ArrowUpRight size={12} aria-hidden="true" />
          </Link>
        )}
        <button
          type="button"
          onClick={onClose}
          data-testid="artifact-pane-close"
          className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-ink-tertiary transition-colors hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          title="Close pane"
        >
          {closeIcon}
          <span className="sr-only">Close</span>
        </button>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4">
        {artifact ? (
          <div key={`${artifact.kind}:${artifact.refId ?? ''}`} data-testid="artifact-pane-body" data-kind={artifact.kind}>
            {renderArtifact(artifact, { size: 'pane', onIntent })}
          </div>
        ) : (
          <div className="flex h-full flex-col items-center justify-center gap-2 px-6 text-center">
            <div className="h-10 w-10 rounded-xl border border-dashed border-line" aria-hidden="true" />
            <p className="text-caption text-ink-secondary">Nothing open yet.</p>
            <p className="text-micro text-ink-tertiary">
              Cards the agents attach to their replies open here. Click any card in the thread.
            </p>
          </div>
        )}
      </div>
    </>
  );
}
