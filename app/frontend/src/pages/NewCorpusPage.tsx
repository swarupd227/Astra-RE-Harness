import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, FileCode, GitBranch, Loader2, Upload, X } from 'lucide-react';
import { ApiError, api, type IngestResult, type SpecSchemaSummary } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Button } from '@/components/Button';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';

type Mode = 'upload' | 'git';

// .zip stays whitelisted for every language because the API unpacks
// archives server-side before per-file extension detection.
const ARCHIVE_EXTS = ['.zip'];

function extensionsForSchema(schema: SpecSchemaSummary | undefined): string[] {
  const exts = schema?.supportedSourceExtensions ?? [];
  return [...exts, ...ARCHIVE_EXTS];
}

function isAcceptable(name: string, exts: string[]): boolean {
  const lower = name.toLowerCase();
  return exts.some((e) => lower.endsWith(e));
}

export function NewCorpusPage() {
  const navigate = useNavigate();
  const qc = useQueryClient();

  const [mode, setMode] = useState<Mode>('upload');
  const [name, setName] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [dragging, setDragging] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  // Git fields
  const [gitUrl, setGitUrl] = useState('');
  const [branch, setBranch] = useState('');
  const [sourceRoot, setSourceRoot] = useState('');

  // Phase 9.2.a — language picker. The picked schema drives the file-
  // extension filter on the dropzone. Default `''` means "auto-detect"
  // (accept any registered language's extensions).
  const [schemaId, setSchemaId] = useState<string>('');
  const schemasQuery = useQuery({
    queryKey: ['spec-schemas'],
    queryFn: () => api.listSpecSchemas(),
  });
  const schemas = schemasQuery.data?.data ?? [];
  const selectedSchema = useMemo(
    () => schemas.find((s) => s.id === schemaId),
    [schemas, schemaId],
  );
  // When auto-detect is active, accept any of the registered schemas'
  // extensions; otherwise filter strictly to the picked schema.
  const acceptedExts = useMemo(() => {
    if (selectedSchema) return extensionsForSchema(selectedSchema);
    const all = new Set<string>(ARCHIVE_EXTS);
    for (const s of schemas) for (const e of s.supportedSourceExtensions) all.add(e);
    return Array.from(all);
  }, [selectedSchema, schemas]);

  const ingest = useMutation<IngestResult, ApiError>({
    mutationFn: async () => {
      if (mode === 'upload') return api.ingestUpload(name, files);
      return api.ingestGit({
        name,
        url: gitUrl,
        branch: branch.trim() || undefined,
        sourceRoot: sourceRoot.trim() || undefined,
      });
    },
    onSuccess: (result) => {
      qc.invalidateQueries({ queryKey: ['corpora'] });
      if (result.state === 'PARSED') {
        // Brief visual confirmation before navigating to the detail page.
        setTimeout(() => navigate(`/projects/${result.corpusId}`), 700);
      }
    },
  });

  const canSubmit =
    !ingest.isPending &&
    !ingest.isSuccess &&
    name.trim().length > 0 &&
    (mode === 'upload' ? files.length > 0 : gitUrl.trim().length > 0);

  function handleFiles(picked: FileList | null) {
    if (!picked) return;
    const accepted: File[] = [];
    for (const f of picked) {
      if (isAcceptable(f.name, acceptedExts)) accepted.push(f);
    }
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
    <div className="mx-auto max-w-[820px] space-y-6 p-6 lg:p-10">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase C.1 · Stage 1 · Ingest
        </p>
        <h1 className="mt-2 text-display font-semibold text-ink-primary">Connect a new project</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Upload source files (or a <span className="font-mono">.zip</span> archive) or
          point at a Git repository. The parser sidecar runs the appropriate
          per-language parser against every file to extract the subroutine
          inventory. Pick a source language to tighten the file filter, or leave
          on <em>auto-detect</em> to accept any of the registered languages.
        </p>
      </header>

      <Card>
        <CardHeader title="Source" description="Pick one. Both paths end at the same PARSED state." />
        <CardBody className="space-y-5">
          <div role="tablist" aria-label="Ingest source type" className="flex gap-2">
            <button
              role="tab"
              aria-selected={mode === 'upload'}
              onClick={() => setMode('upload')}
              className={tabClass(mode === 'upload')}
              data-testid="ingest-tab-upload"
              type="button"
            >
              <Upload className="h-4 w-4" aria-hidden="true" /> Upload files
            </button>
            <button
              role="tab"
              aria-selected={mode === 'git'}
              onClick={() => setMode('git')}
              className={tabClass(mode === 'git')}
              data-testid="ingest-tab-git"
              type="button"
            >
              <GitBranch className="h-4 w-4" aria-hidden="true" /> Git URL
            </button>
          </div>

          <FieldRow label="Project name" htmlFor="corpus-name" hint="Displayed in the projects list. Must be unique.">
            <input
              id="corpus-name"
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. legacy inventory subset"
              className={inputClass}
              data-testid="corpus-name"
              disabled={ingest.isPending || ingest.isSuccess}
            />
          </FieldRow>

          {/* Phase 9.2.a — language picker. Default 'Auto-detect' keeps
              the existing extension-based heuristic; picking a specific
              language tightens the file filter to that schema's
              extensions only. */}
          <FieldRow
            label="Source language"
            htmlFor="corpus-schema"
            hint={
              selectedSchema
                ? `Accepts ${selectedSchema.supportedSourceExtensions.join(', ')} (+ .zip)`
                : 'Auto-detect from file extensions — accepts any registered language.'
            }
          >
            <select
              id="corpus-schema"
              value={schemaId}
              onChange={(e) => setSchemaId(e.target.value)}
              className={inputClass}
              data-testid="corpus-schema"
              disabled={ingest.isPending || ingest.isSuccess || schemasQuery.isPending}
            >
              <option value="">Auto-detect from file extensions</option>
              {schemas.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.displayName}
                </option>
              ))}
            </select>
          </FieldRow>

          {mode === 'upload' ? (
            <FieldRow label="Files" htmlFor="corpus-files" hint={
              selectedSchema
                ? `${selectedSchema.supportedSourceExtensions.slice(0, 5).join(', ')}${
                    selectedSchema.supportedSourceExtensions.length > 5 ? '…' : ''
                  } — or a single .zip`
                : 'Source files matching any registered language — or a single .zip'
            }>
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
                data-testid="dropzone"
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
                    id="corpus-files"
                    type="file"
                    multiple
                    accept={acceptedExts.join(',')}
                    className="hidden"
                    onChange={(e) => handleFiles(e.target.files)}
                    data-testid="file-input"
                  />
                </div>
              </div>
              {files.length > 0 && (
                <ul className="mt-3 divide-y divide-border-subtle rounded-md border border-border-subtle bg-raised">
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
            </FieldRow>
          ) : (
            <>
              <FieldRow label="Git URL" htmlFor="git-url" hint="HTTPS clone URL. The API container must be able to reach this host.">
                <input
                  id="git-url"
                  type="url"
                  value={gitUrl}
                  onChange={(e) => setGitUrl(e.target.value)}
                  placeholder="https://github.com/your-org/legacy-source.git"
                  className={inputClass}
                  data-testid="git-url"
                  disabled={ingest.isPending || ingest.isSuccess}
                />
              </FieldRow>
              <div className="grid gap-4 sm:grid-cols-2">
                <FieldRow label="Branch (optional)" htmlFor="git-branch" hint="Default branch if blank.">
                  <input
                    id="git-branch"
                    type="text"
                    value={branch}
                    onChange={(e) => setBranch(e.target.value)}
                    placeholder="main"
                    className={inputClass}
                    disabled={ingest.isPending || ingest.isSuccess}
                  />
                </FieldRow>
                <FieldRow label="Source root (optional)" htmlFor="git-root" hint="Sub-directory containing the source tree.">
                  <input
                    id="git-root"
                    type="text"
                    value={sourceRoot}
                    onChange={(e) => setSourceRoot(e.target.value)}
                    placeholder="src/legacy"
                    className={inputClass}
                    disabled={ingest.isPending || ingest.isSuccess}
                  />
                </FieldRow>
              </div>
            </>
          )}

          <div className="flex items-center justify-end gap-3">
            <Button
              variant="ghost"
              onClick={() => navigate('/projects')}
              disabled={ingest.isPending}
            >
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={() => ingest.mutate()}
              disabled={!canSubmit}
              loading={ingest.isPending}
              data-testid="ingest-submit"
            >
              {ingest.isPending
                ? 'Ingesting…'
                : ingest.isSuccess
                  ? 'Ingested'
                  : mode === 'upload'
                    ? 'Ingest files'
                    : 'Clone & ingest'}
            </Button>
          </div>
        </CardBody>
      </Card>

      {ingest.isPending && <ProgressCard label="Hashing, uploading, calling parser…" />}

      {ingest.isError && (
        <ErrorBlock
          title="Ingest failed"
          message={ingest.error.message}
          onRetry={() => ingest.reset()}
        />
      )}

      {ingest.isSuccess && (
        <ResultCard result={ingest.data} onOpen={() => navigate(`/projects/${ingest.data.corpusId}`)} />
      )}
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────
// Sub-components
// ────────────────────────────────────────────────────────────────────

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

function FieldRow({
  label,
  htmlFor,
  hint,
  children,
}: {
  label: string;
  htmlFor: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label htmlFor={htmlFor} className="text-body font-medium text-ink-primary">
        {label}
      </label>
      {hint && <p className="mt-0.5 text-caption text-ink-tertiary">{hint}</p>}
      {children}
    </div>
  );
}

function ProgressCard({ label }: { label: string }) {
  return (
    <Card>
      <CardBody>
        <div className="flex items-center gap-3" role="status" aria-live="polite">
          <Loader2 className="h-5 w-5 animate-spin text-accent" aria-hidden="true" />
          <p className="text-body text-ink-secondary">{label}</p>
        </div>
      </CardBody>
    </Card>
  );
}

function ResultCard({ result, onOpen }: { result: IngestResult; onOpen: () => void }) {
  const isPersisted = result.state === 'PARSED';
  return (
    <Card data-testid="ingest-result">
      <CardBody>
        <header className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            {isPersisted ? (
              <CheckCircle2 className="h-5 w-5 text-status-signed" aria-hidden="true" />
            ) : (
              <Loader2 className="h-5 w-5 animate-spin text-status-failed" aria-hidden="true" />
            )}
            <h3 className="text-h-md font-semibold text-ink-primary">
              {isPersisted ? 'Project parsed' : `State: ${result.state}`}
            </h3>
          </div>
          <Badge tone={isPersisted ? 'signed' : 'failed'}>{result.state}</Badge>
        </header>
        <dl className="mt-4 grid grid-cols-3 gap-4 text-body">
          <Stat label="Files" value={result.fileCount.toString()} />
          <Stat label="Lines of code" value={result.totalLoc.toLocaleString()} />
          <Stat label="Subroutines" value={result.subroutineCount.toString()} />
        </dl>
        {result.warnings.length > 0 && (
          <details className="mt-4 rounded-md border border-border-subtle bg-sunken p-3">
            <summary className="cursor-pointer text-body font-medium text-ink-primary">
              {result.warnings.length} parse warning{result.warnings.length === 1 ? '' : 's'}
            </summary>
            <ul className="mt-2 list-disc space-y-1 pl-5 font-mono text-caption text-ink-secondary">
              {result.warnings.map((w, i) => (
                <li key={i}>{w}</li>
              ))}
            </ul>
          </details>
        )}
        {result.errorMessage && (
          <p className="mt-3 font-mono text-caption text-status-failed">{result.errorMessage}</p>
        )}
        {isPersisted && (
          <div className="mt-5 flex justify-end">
            <Button variant="primary" onClick={onOpen} data-testid="open-corpus">
              Open project
            </Button>
          </div>
        )}
      </CardBody>
    </Card>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-caption text-ink-tertiary">{label}</dt>
      <dd className="mt-0.5 font-mono text-h-md font-semibold text-ink-primary">{value}</dd>
    </div>
  );
}
