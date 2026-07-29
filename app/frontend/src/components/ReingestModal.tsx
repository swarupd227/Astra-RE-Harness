import { useMemo, useRef, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { CheckCircle2, FileCode, GitBranch, RefreshCw, Upload, X } from 'lucide-react';
import { ApiError, api, type ReingestResult } from '@/lib/api';
import { Button } from '@/components/Button';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { useModalA11y } from '@/hooks/useModalA11y';

type Mode = 'upload' | 'git';

// Was hardcoded to Fortran-only extensions, so re-ingesting any of the other
// 9 registered languages silently rejected every uploaded file. Now sourced
// from the registered schemas — same as NewCorpusPage's auto-detect mode.
function isAcceptable(name: string, exts: string[]): boolean {
  const lower = name.toLowerCase();
  return exts.some((e) => lower.endsWith(e));
}

/**
 * Phase C.3 — re-sync a corpus. Re-uses the ingest UI shape but routes to
 * /reingest/{upload,git}. After success, surfaces the carry-forward and
 * supersession counts so the engineer immediately sees what changed.
 */
export function ReingestModal({
  open,
  onClose,
  onSuccess,
  corpusId,
  corpusName,
  defaultSourceType,
  defaultGitUrl,
  defaultBranch,
  defaultSourceRoot,
}: {
  open: boolean;
  onClose: () => void;
  onSuccess: (result: ReingestResult) => void;
  corpusId: string;
  corpusName: string;
  defaultSourceType: string;            // "upload" | "git"
  defaultGitUrl?: string | null;
  defaultBranch?: string | null;
  defaultSourceRoot?: string | null;
}) {
  const initialMode: Mode = defaultSourceType === 'git' ? 'git' : 'upload';
  const [mode, setMode] = useState<Mode>(initialMode);
  const [files, setFiles] = useState<File[]>([]);
  const [rejected, setRejected] = useState<string[]>([]);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const schemasQuery = useQuery({
    queryKey: ['spec-schemas'],
    queryFn: () => api.listSpecSchemas(),
  });
  const acceptedExts = useMemo(() => {
    const all = new Set<string>(['.zip']);
    for (const s of schemasQuery.data?.data ?? []) {
      for (const e of s.supportedSourceExtensions) all.add(e);
    }
    return Array.from(all);
  }, [schemasQuery.data]);

  const [gitUrl, setGitUrl] = useState(defaultGitUrl ?? '');
  const [branch, setBranch] = useState(defaultBranch ?? '');
  const [sourceRoot, setSourceRoot] = useState(defaultSourceRoot ?? '');

  const reingest = useMutation<ReingestResult, ApiError>({
    mutationFn: async () => {
      if (mode === 'upload') return api.reingestUpload(corpusId, files);
      return api.reingestGit(corpusId, {
        url: gitUrl,
        branch: branch.trim() || undefined,
        sourceRoot: sourceRoot.trim() || undefined,
      });
    },
  });

  function handleClose() {
    if (reingest.isPending) return;
    if (reingest.isSuccess) onSuccess(reingest.data);
    onClose();
  }

  const dialogRef = useModalA11y<HTMLDivElement>(open, handleClose);

  if (!open) return null;

  const canSubmit =
    !reingest.isPending &&
    !reingest.isSuccess &&
    (mode === 'upload' ? files.length > 0 : gitUrl.trim().length > 0);

  function handleFiles(picked: FileList | null) {
    if (!picked) return;
    const accepted: File[] = [];
    const rej: string[] = [];
    for (const f of picked) {
      if (isAcceptable(f.name, acceptedExts)) accepted.push(f);
      else rej.push(f.name);
    }
    setRejected(rej);
    if (accepted.length === 0) return;
    setFiles((prev) => {
      const seen = new Set(prev.map((p) => `${p.name}:${p.size}`));
      const merged = [...prev];
      for (const f of accepted) {
        const key = `${f.name}:${f.size}`;
        if (!seen.has(key)) merged.push(f);
      }
      return merged;
    });
  }

  function removeFile(idx: number) {
    setFiles((prev) => prev.filter((_, i) => i !== idx));
  }

  return (
    <div
      ref={dialogRef}
      tabIndex={-1}
      className="fixed inset-0 z-50 flex items-center justify-center outline-none motion-safe:animate-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby="reingest-modal-title"
      onClick={handleClose}
    >
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      <div
        className="relative w-[720px] max-w-[92vw] overflow-hidden rounded-lg border border-border-subtle bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between border-b border-border-subtle px-6 py-4">
          <div className="flex items-start gap-3">
            <span className="flex h-9 w-9 items-center justify-center rounded-md bg-accent-muted text-accent">
              <RefreshCw className="h-5 w-5" aria-hidden="true" />
            </span>
            <div>
              <h2 id="reingest-modal-title" className="text-h-md font-semibold text-ink-primary">
                Re-sync project
              </h2>
              <p className="mt-1 text-body text-ink-secondary">
                Ingest a new version of <span className="font-mono text-ink-primary">{corpusName}</span>.
                Specs whose source is unchanged are carried forward; specs whose source has changed
                are marked <Badge tone="superseded" className="ml-1">SUPERSEDED</Badge>.
              </p>
            </div>
          </div>
          <button
            type="button"
            aria-label="Close"
            onClick={handleClose}
            className="rounded p-1 text-ink-tertiary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
          >
            <X className="h-5 w-5" />
          </button>
        </header>

        <div className="space-y-5 px-6 py-5">
          <div role="tablist" aria-label="Re-sync source type" className="flex gap-2">
            <button
              role="tab"
              type="button"
              aria-selected={mode === 'upload'}
              onClick={() => setMode('upload')}
              className={tabClass(mode === 'upload')}
              data-testid="reingest-tab-upload"
            >
              <Upload className="h-4 w-4" aria-hidden="true" /> Upload files
            </button>
            <button
              role="tab"
              type="button"
              aria-selected={mode === 'git'}
              onClick={() => setMode('git')}
              className={tabClass(mode === 'git')}
              data-testid="reingest-tab-git"
            >
              <GitBranch className="h-4 w-4" aria-hidden="true" /> Git URL
            </button>
          </div>

          {mode === 'upload' ? (
            <div>
              <div
                onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
                onDragLeave={() => setDragging(false)}
                onDrop={(e) => {
                  e.preventDefault();
                  setDragging(false);
                  handleFiles(e.dataTransfer.files);
                }}
                className={
                  'rounded-md border-2 border-dashed transition-colors duration-fast ' +
                  (dragging ? 'border-accent bg-accent/5' : 'border-border bg-sunken')
                }
                data-testid="reingest-dropzone"
              >
                <div className="flex flex-col items-center justify-center gap-2 px-6 py-8 text-center">
                  <Upload className="h-6 w-6 text-ink-tertiary" aria-hidden="true" />
                  <p className="text-body text-ink-secondary">
                    Drag and drop, or{' '}
                    <button
                      type="button"
                      onClick={() => inputRef.current?.click()}
                      className="font-medium text-accent underline-offset-2 hover:underline"
                    >
                      browse
                    </button>
                    .
                  </p>
                  <input
                    ref={inputRef}
                    type="file"
                    multiple
                    accept={acceptedExts.join(',')}
                    className="hidden"
                    onChange={(e) => handleFiles(e.target.files)}
                    data-testid="reingest-file-input"
                  />
                </div>
              </div>
              {rejected.length > 0 && (
                <div
                  role="alert"
                  className="mt-3 rounded-md border border-status-scaffolded/40 bg-[#FBF1D9] px-4 py-3 text-caption text-status-scaffolded"
                  data-testid="reingest-rejected-files"
                >
                  <p className="font-medium">
                    Skipped {rejected.length} unsupported {rejected.length === 1 ? 'file' : 'files'}:{' '}
                    <span className="font-mono">{rejected.slice(0, 5).join(', ')}</span>
                    {rejected.length > 5 ? ` +${rejected.length - 5} more` : ''}
                  </p>
                  <p className="mt-0.5 text-ink-secondary">
                    Accepted: <span className="font-mono">{acceptedExts.join(', ')}</span>
                  </p>
                </div>
              )}
              {files.length > 0 && (
                <ul className="mt-3 max-h-48 overflow-y-auto divide-y divide-border-subtle rounded-md border border-border-subtle bg-raised">
                  {files.map((f, i) => (
                    <li key={`${f.name}:${i}`} className="flex items-center gap-3 px-4 py-2 text-body">
                      <FileCode className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                      <span className="font-mono text-ink-primary">{f.name}</span>
                      <span className="ml-auto font-mono text-caption text-ink-tertiary">
                        {(f.size / 1024).toFixed(1)} KiB
                      </span>
                      <button
                        type="button"
                        onClick={() => removeFile(i)}
                        className="rounded p-1 text-ink-tertiary hover:bg-sunken hover:text-status-failed focus-visible:outline-2 focus-visible:outline-ink-primary"
                        aria-label={`Remove ${f.name}`}
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          ) : (
            <>
              <Field label="Git URL" id="reingest-git-url">
                <input
                  id="reingest-git-url"
                  type="url"
                  value={gitUrl}
                  onChange={(e) => setGitUrl(e.target.value)}
                  placeholder="https://github.com/your-org/legacy-fortran.git"
                  className={inputClass}
                  data-testid="reingest-git-url"
                  disabled={reingest.isPending || reingest.isSuccess}
                />
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Branch (optional)" id="reingest-branch">
                  <input
                    id="reingest-branch"
                    type="text"
                    value={branch}
                    onChange={(e) => setBranch(e.target.value)}
                    placeholder="main"
                    className={inputClass}
                    disabled={reingest.isPending || reingest.isSuccess}
                  />
                </Field>
                <Field label="Source root (optional)" id="reingest-root">
                  <input
                    id="reingest-root"
                    type="text"
                    value={sourceRoot}
                    onChange={(e) => setSourceRoot(e.target.value)}
                    placeholder="src/legacy"
                    className={inputClass}
                    disabled={reingest.isPending || reingest.isSuccess}
                  />
                </Field>
              </div>
            </>
          )}

          {reingest.isError && (
            <ErrorBlock
              title="Re-sync failed"
              message={reingest.error.message}
              onRetry={() => reingest.reset()}
            />
          )}

          {reingest.isSuccess && <Outcome result={reingest.data} />}
        </div>

        <footer className="flex items-center justify-end gap-3 border-t border-border-subtle bg-sunken px-6 py-4">
          {reingest.isSuccess ? (
            <Button
              variant="primary"
              onClick={() => {
                onSuccess(reingest.data);
                onClose();
              }}
              data-testid="reingest-done"
            >
              Done
            </Button>
          ) : (
            <>
              <Button variant="ghost" onClick={handleClose} disabled={reingest.isPending}>
                Cancel
              </Button>
              <Button
                variant="primary"
                onClick={() => reingest.mutate()}
                disabled={!canSubmit}
                loading={reingest.isPending}
                data-testid="reingest-submit"
              >
                {reingest.isPending ? 'Re-syncing…' : 'Re-sync'}
              </Button>
            </>
          )}
        </footer>
      </div>
    </div>
  );
}

function tabClass(active: boolean): string {
  return (
    'inline-flex items-center gap-2 rounded-md border px-3 py-2 text-body font-medium transition-colors duration-fast ' +
    (active
      ? 'border-accent bg-accent/10 text-accent'
      : 'border-border bg-raised text-ink-secondary hover:bg-sunken')
  );
}

const inputClass =
  'mt-1 w-full rounded-md border border-border bg-raised px-3 py-2 font-mono text-body text-ink-primary placeholder:text-ink-tertiary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20 disabled:cursor-not-allowed disabled:opacity-60';

function Field({ label, id, children }: { label: string; id: string; children: React.ReactNode }) {
  return (
    <div>
      <label htmlFor={id} className="text-body font-medium text-ink-primary">
        {label}
      </label>
      {children}
    </div>
  );
}

function Outcome({ result }: { result: ReingestResult }) {
  return (
    <div
      className="rounded-md border border-status-signed/40 bg-[#DCE6F5]/40 p-4"
      data-testid="reingest-outcome"
    >
      <div className="flex items-center gap-2">
        <CheckCircle2 className="h-5 w-5 text-status-signed" aria-hidden="true" />
        <h3 className="text-h-sm font-semibold text-ink-primary">Re-sync complete</h3>
      </div>
      <dl className="mt-3 grid grid-cols-4 gap-3 text-body">
        <Stat label="Files" value={result.fileCount.toString()} />
        <Stat label="Subroutines" value={result.subroutineCount.toString()} />
        <Stat label="Carried fwd" value={result.carriedForwardCount.toString()} tone={result.carriedForwardCount > 0 ? 'signed' : 'neutral'} />
        <Stat label="Superseded" value={result.supersededCount.toString()} tone={result.supersededCount > 0 ? 'failed' : 'neutral'} />
      </dl>
      {result.warnings.length > 0 && (
        <details className="mt-3 rounded-md bg-sunken p-2">
          <summary className="cursor-pointer text-body font-medium text-ink-primary">
            {result.warnings.length} parse warning{result.warnings.length === 1 ? '' : 's'}
          </summary>
          <ul className="mt-2 list-disc space-y-1 pl-5 font-mono text-caption text-ink-secondary">
            {result.warnings.map((w, i) => (<li key={i}>{w}</li>))}
          </ul>
        </details>
      )}
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone?: 'signed' | 'failed' | 'neutral' }) {
  const color = tone === 'signed' ? 'text-status-signed'
    : tone === 'failed' ? 'text-status-failed'
    : 'text-ink-primary';
  return (
    <div>
      <dt className="text-caption text-ink-tertiary">{label}</dt>
      <dd className={`mt-0.5 font-mono text-h-md font-semibold ${color}`}>{value}</dd>
    </div>
  );
}
