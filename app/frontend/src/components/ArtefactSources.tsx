import { useRef, useState, type DragEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ChevronDown, ChevronRight, FileUp, FolderUp, Trash2 } from 'lucide-react';
import { clsx } from 'clsx';
import {
  api,
  type ArtefactSummaryKind,
  type ArtefactUpload,
  type AttachFileResult,
  type AttachResponse,
} from '@/lib/api';
import { fromDrop, fromPicker, formatBytes } from '@/lib/artefactFiles';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';

// The endpoint takes up to 500 files a call; stay well under so a big folder is several calls.
const BATCH = 200;

async function attachInBatches(corpusId: string, uploads: ArtefactUpload[], kind?: string): Promise<AttachResponse> {
  let merged: AttachResponse | null = null;
  for (let i = 0; i < uploads.length; i += BATCH) {
    const r = await api.attachArtefacts(corpusId, uploads.slice(i, i + BATCH), kind);
    merged = merged
      ? {
          ...r,
          counts: {
            added: merged.counts.added + r.counts.added,
            replaced: merged.counts.replaced + r.counts.replaced,
            unchanged: merged.counts.unchanged + r.counts.unchanged,
            rejected: merged.counts.rejected + r.counts.rejected,
          },
          files: [...merged.files, ...r.files],
        }
      : r;
  }
  return merged!;
}

/**
 * What a project holds besides routines: one tile per evidence kind, a place to add more,
 * and — after every add — a line for each file saying what happened to it. Nothing is
 * dropped silently: a refused file comes back with its reason.
 */
export function ArtefactSources({ corpusId, canWrite }: { corpusId: string; canWrite: boolean }) {
  const qc = useQueryClient();
  const summary = useQuery({
    queryKey: ['artefacts', corpusId],
    queryFn: () => api.getArtefacts(corpusId),
  });

  const [open, setOpen] = useState<string | null>(null);
  const [dragging, setDragging] = useState(false);
  const [forcedKind, setForcedKind] = useState('');
  const [result, setResult] = useState<AttachResponse | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const filesInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['artefacts', corpusId] });
    qc.invalidateQueries({ queryKey: ['corpus', corpusId] });
  };

  const attach = useMutation({
    mutationFn: (uploads: ArtefactUpload[]) => attachInBatches(corpusId, uploads, forcedKind || undefined),
    onMutate: () => {
      setResult(null);
      setNotice(null);
    },
    onSuccess: (r) => {
      setResult(r);
      refresh();
    },
  });

  const remove = useMutation({
    mutationFn: (path: string) => api.removeArtefact(corpusId, path),
    onSuccess: () => {
      setConfirming(null);
      refresh();
    },
  });

  const add = (uploads: ArtefactUpload[]) => {
    if (uploads.length === 0) {
      setResult(null);
      setNotice('Nothing to add — that selection holds no files.');
      return;
    }
    attach.mutate(uploads);
  };

  const onDrop = async (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setDragging(false);
    if (!canWrite || attach.isPending) return;
    add(await fromDrop(e.dataTransfer));
  };

  if (summary.isPending) return <Skeleton className="h-48 w-full" />;
  if (summary.isError) {
    return (
      <ErrorBlock title="Could not load sources" message={summary.error.message} onRetry={() => summary.refetch()} />
    );
  }

  const kinds = summary.data.kinds;
  const shown = kinds.filter((k) => k.core || k.enabled || k.fileCount > 0);
  const enabledOptional = kinds.filter((k) => !k.core && k.enabled);
  const expanded = shown.find((k) => k.id === open && !k.core && k.fileCount > 0) ?? null;
  const kindLabel = (id: string | null) => kinds.find((k) => k.id === id)?.label ?? id ?? '';

  return (
    <Card data-testid="artefact-sources">
      <CardHeader
        title="Sources"
        description="The evidence this project holds. Source code is read into routines; everything else is kept as evidence to cite."
        action={
          <Link
            to={`/corpora/${corpusId}/settings`}
            className="text-body text-ink-secondary underline-offset-2 hover:text-ink-primary hover:underline focus-visible:outline-2 focus-visible:outline-volt"
            data-testid="artefact-sources-settings"
          >
            Choose kinds
          </Link>
        }
      />
      <CardBody className="space-y-5">
        <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" data-testid="artefact-kind-tiles">
          {shown.map((k) => (
            <KindTile
              key={k.id}
              kind={k}
              selected={open === k.id}
              onSelect={() => setOpen(open === k.id ? null : k.id)}
            />
          ))}
        </ul>

        {expanded && (
          <FileList
            kind={expanded}
            canWrite={canWrite}
            confirming={confirming}
            removing={remove.isPending}
            removeError={remove.isError ? remove.error.message : null}
            onAsk={setConfirming}
            onRemove={(p) => remove.mutate(p)}
          />
        )}

        {enabledOptional.length === 0 ? (
          <p className="rounded-md border border-line-subtle bg-sunken px-4 py-3 text-body text-ink-secondary" data-testid="artefact-none-enabled">
            Only source code is switched on. To keep database scripts, tickets, transcripts or other
            evidence with this project,{' '}
            <Link to={`/corpora/${corpusId}/settings`} className="text-ink-primary underline underline-offset-2">
              choose the kinds in settings
            </Link>
            .
          </p>
        ) : !canWrite ? (
          <p className="text-caption text-ink-tertiary" data-testid="artefact-readonly">
            Your role can read sources but not add or remove them.
          </p>
        ) : (
          <div
            role="group"
            aria-label="Add evidence"
            onDragOver={(e) => {
              e.preventDefault();
              if (!dragging) setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            className={clsx(
              'rounded-lg border border-dashed px-5 py-5 transition-colors duration-fast',
              dragging ? 'border-volt bg-volt/5' : 'border-line bg-sunken',
            )}
            data-testid="artefact-dropzone"
          >
            <div className="flex flex-wrap items-center gap-3">
              <div className="min-w-0 flex-1">
                <p className="text-body font-medium text-ink-primary">Add evidence</p>
                <p className="text-caption text-ink-secondary">
                  Drop files or a folder here. A file with the same path replaces the earlier one.
                </p>
              </div>
              {enabledOptional.length > 1 && (
                <label className="flex items-center gap-2 text-caption text-ink-secondary">
                  Treat as
                  <select
                    value={forcedKind}
                    onChange={(e) => setForcedKind(e.target.value)}
                    disabled={attach.isPending}
                    className="h-8 rounded-md border border-line bg-raised px-2 text-body text-ink-primary focus-visible:outline-2 focus-visible:outline-volt"
                    data-testid="artefact-kind-override"
                  >
                    <option value="">Detect by file type</option>
                    {enabledOptional.map((k) => (
                      <option key={k.id} value={k.id}>{k.label}</option>
                    ))}
                  </select>
                </label>
              )}
              <Button
                variant="secondary"
                size="sm"
                loading={attach.isPending}
                onClick={() => filesInput.current?.click()}
                data-testid="artefact-choose-files"
              >
                <FileUp className="h-4 w-4" aria-hidden="true" />
                Choose files
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={attach.isPending}
                onClick={() => folderInput.current?.click()}
                data-testid="artefact-choose-folder"
              >
                <FolderUp className="h-4 w-4" aria-hidden="true" />
                Choose folder
              </Button>
            </div>
            <input
              ref={filesInput}
              type="file"
              multiple
              className="sr-only"
              tabIndex={-1}
              aria-hidden="true"
              data-testid="artefact-file-input"
              onChange={(e) => {
                add(fromPicker(e.target.files));
                e.target.value = '';
              }}
            />
            <input
              ref={folderInput}
              type="file"
              multiple
              className="sr-only"
              tabIndex={-1}
              aria-hidden="true"
              data-testid="artefact-folder-input"
              {...({ webkitdirectory: '' } as Record<string, string>)}
              onChange={(e) => {
                add(fromPicker(e.target.files));
                e.target.value = '';
              }}
            />
          </div>
        )}

        {attach.isError && (
          <p className="text-body text-status-fail" role="alert" data-testid="artefact-attach-error">
            {attach.error.message}
          </p>
        )}
        {notice && <p className="text-body text-ink-secondary" data-testid="artefact-notice">{notice}</p>}
        {result && <AttachResult result={result} kindLabel={kindLabel} onDismiss={() => setResult(null)} />}
      </CardBody>
    </Card>
  );
}

function KindTile({ kind, selected, onSelect }: { kind: ArtefactSummaryKind; selected: boolean; onSelect: () => void }) {
  const size = kind.fileCount === 0 ? null : `${formatBytes(kind.totalBytes)}${kind.sizeKnown ? '' : '+'}`;
  const body = (
    <>
      <span className="flex items-center gap-2">
        <span className="text-body font-medium text-ink-primary">{kind.label}</span>
        {!kind.core && !kind.enabled && <Badge tone="neutral">Switched off</Badge>}
        <Badge tone={kind.trustTier === 'structured' ? 'info' : 'neutral'}>
          {kind.trustTier === 'structured' ? 'Structured' : 'Narrative'}
        </Badge>
      </span>
      <span className="mt-2 flex items-baseline gap-2">
        <span className="font-mono text-h-md font-semibold tabular-nums text-ink-primary" data-testid={`artefact-count-${kind.id}`}>
          {kind.fileCount.toLocaleString()}
        </span>
        <span className="text-caption text-ink-secondary">
          file{kind.fileCount === 1 ? '' : 's'}
          {size ? ` · ${size}` : ''}
        </span>
      </span>
    </>
  );

  // Source code is listed on the page below; only the other kinds open a file list here.
  return (
    <li data-testid={`artefact-kind-tile-${kind.id}`}>
      {kind.core ? (
        <div className="h-full rounded-md border border-line-subtle bg-raised px-4 py-3">{body}</div>
      ) : (
        <button
          type="button"
          onClick={onSelect}
          aria-expanded={selected}
          disabled={kind.fileCount === 0}
          className={clsx(
            'flex h-full w-full flex-col items-start rounded-md border bg-raised px-4 py-3 text-left transition-colors duration-fast focus-visible:outline-2 focus-visible:outline-volt',
            selected ? 'border-volt' : 'border-line-subtle hover:border-line',
            kind.fileCount === 0 && 'cursor-default opacity-70 hover:border-line-subtle',
          )}
        >
          {body}
          {kind.fileCount > 0 && (
            <span className="mt-2 flex items-center gap-1 text-micro text-ink-tertiary">
              {selected ? <ChevronDown className="h-3 w-3" aria-hidden="true" /> : <ChevronRight className="h-3 w-3" aria-hidden="true" />}
              {selected ? 'Hide files' : 'Show files'}
            </span>
          )}
        </button>
      )}
    </li>
  );
}

function FileList({
  kind,
  canWrite,
  confirming,
  removing,
  removeError,
  onAsk,
  onRemove,
}: {
  kind: ArtefactSummaryKind;
  canWrite: boolean;
  confirming: string | null;
  removing: boolean;
  removeError: string | null;
  onAsk: (path: string | null) => void;
  onRemove: (path: string) => void;
}) {
  return (
    <div className="rounded-md border border-line-subtle" data-testid={`artefact-files-${kind.id}`}>
      <p className="border-b border-line-subtle px-4 py-2 text-caption text-ink-secondary">
        {kind.label} in this version
      </p>
      <ul className="max-h-72 divide-y divide-line-subtle overflow-auto">
        {kind.files.map((f) => (
          <li key={f.id} className="flex items-center gap-3 px-4 py-2" data-testid="artefact-file-row">
            <span className="min-w-0 flex-1 truncate font-mono text-caption text-ink-primary" title={f.path}>{f.path}</span>
            <span className="shrink-0 font-mono text-micro text-ink-tertiary">
              {formatBytes(f.sizeBytes)}
              {f.lineCount > 0 ? ` · ${f.lineCount.toLocaleString()} lines` : ''}
            </span>
            {canWrite &&
              (confirming === f.path ? (
                <span className="flex shrink-0 items-center gap-2">
                  <Button variant="destructive" size="sm" loading={removing} onClick={() => onRemove(f.path)} data-testid="artefact-remove-confirm">
                    Remove
                  </Button>
                  <Button variant="ghost" size="sm" disabled={removing} onClick={() => onAsk(null)}>
                    Keep
                  </Button>
                </span>
              ) : (
                <button
                  type="button"
                  onClick={() => onAsk(f.path)}
                  aria-label={`Remove ${f.path}`}
                  className="shrink-0 rounded-md p-1.5 text-ink-tertiary transition-colors duration-fast hover:bg-sunken hover:text-status-fail focus-visible:outline-2 focus-visible:outline-volt"
                  data-testid="artefact-remove"
                >
                  <Trash2 className="h-4 w-4" aria-hidden="true" />
                </button>
              ))}
          </li>
        ))}
      </ul>
      {removeError && (
        <p className="border-t border-line-subtle px-4 py-2 text-caption text-status-fail" role="alert">{removeError}</p>
      )}
    </div>
  );
}

const STATUS_ORDER: AttachFileResult['status'][] = ['rejected', 'added', 'replaced', 'unchanged'];
const STATUS_WORD: Record<AttachFileResult['status'], string> = {
  added: 'Added',
  replaced: 'Replaced',
  unchanged: 'Unchanged',
  rejected: 'Not added',
};

function AttachResult({
  result,
  kindLabel,
  onDismiss,
}: {
  result: AttachResponse;
  kindLabel: (id: string | null) => string;
  onDismiss: () => void;
}) {
  const { counts } = result;
  const files = [...result.files].sort(
    (a, b) => STATUS_ORDER.indexOf(a.status) - STATUS_ORDER.indexOf(b.status) || a.path.localeCompare(b.path),
  );
  return (
    <div className="rounded-md border border-line-subtle" role="status" data-testid="artefact-results">
      <div className="flex flex-wrap items-center gap-2 border-b border-line-subtle px-4 py-2">
        <span className="mr-auto text-body font-medium text-ink-primary">
          {counts.rejected === 0 ? 'Done' : counts.added + counts.replaced + counts.unchanged === 0 ? 'Nothing was added' : 'Done, with some files not added'}
        </span>
        {counts.added > 0 && <Badge tone="success">{counts.added} added</Badge>}
        {counts.replaced > 0 && <Badge tone="info">{counts.replaced} replaced</Badge>}
        {counts.unchanged > 0 && <Badge tone="neutral">{counts.unchanged} unchanged</Badge>}
        {counts.rejected > 0 && <Badge tone="failed">{counts.rejected} not added</Badge>}
        <Button variant="ghost" size="sm" onClick={onDismiss} data-testid="artefact-results-dismiss">Dismiss</Button>
      </div>
      <ul className="max-h-72 divide-y divide-line-subtle overflow-auto">
        {files.map((f) => (
          <li key={f.path} className="px-4 py-2" data-testid={`artefact-result-${f.status}`}>
            <div className="flex items-center gap-3">
              <span className="w-20 shrink-0 text-micro font-semibold uppercase tracking-wider text-ink-tertiary">
                {STATUS_WORD[f.status]}
              </span>
              <span className="min-w-0 flex-1 truncate font-mono text-caption text-ink-primary" title={f.path}>{f.path}</span>
              {f.kind && f.status !== 'rejected' && (
                <span className="shrink-0 text-micro text-ink-secondary">{kindLabel(f.kind)}</span>
              )}
            </div>
            {f.message && <p className="mt-1 pl-[5.75rem] text-caption text-status-fail">{f.message}</p>}
            {f.ambiguous && f.status !== 'rejected' && (
              <p className="mt-1 pl-[5.75rem] text-caption text-ink-secondary">
                Several kinds take this file type; it was kept as {kindLabel(f.kind)}. Use “Treat as” to choose another.
              </p>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
