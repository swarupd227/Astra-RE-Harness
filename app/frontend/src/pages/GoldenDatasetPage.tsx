import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pencil, Play, Plus, RefreshCw, Save, Trash2, X } from 'lucide-react';
import { clsx } from 'clsx';
import {
  api,
  ApiError,
  type GoldenDatasetEntry,
  type GoldenDatasetExpectedClaim,
  type GoldenDatasetSummary,
  type GoldenDatasetUpsert,
} from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Card, CardBody } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { PageHero } from '@/components/PageHero';

/**
 * Phase 6.0 — Golden Dataset admin page.
 *
 * Lists every entry, shows the latest score per entry plus the aggregate
 * (matched / total across the corpus), and lets admins:
 *   - Run a single entry's scorer
 *   - Run the scorer across all entries (one schema or both)
 *   - Create / edit / delete entries (the same shape the seed YAML has)
 *
 * Non-admin personas see the read-only view: the corpus is a public
 * artifact — its existence is part of the "we measure ourselves" story
 * — but only admins can mutate it or trigger a score run.
 */
export function GoldenDatasetPage() {
  const queryClient = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  const list = useQuery({
    queryKey: ['golden-dataset'],
    queryFn: () => api.listGoldenDataset(),
    staleTime: 0,
  });

  const [schemaFilter, setSchemaFilter] = useState<string>('all');
  const [openEntryId, setOpenEntryId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const schemas = useMemo(
    () => uniq(list.data?.data.map((e) => e.schemaId) ?? []),
    [list.data],
  );
  const filtered = useMemo(() => {
    if (!list.data) return [];
    return list.data.data.filter((e) => schemaFilter === 'all' || e.schemaId === schemaFilter);
  }, [list.data, schemaFilter]);

  // Aggregate: count latest-run matched / total across visible (non-deprecated)
  // entries. Skips entries that haven't been scored yet.
  const aggregate = useMemo(() => {
    let matched = 0;
    let total = 0;
    let scored = 0;
    for (const e of filtered) {
      if (e.status === 'deprecated' || !e.latestRun) continue;
      matched += e.latestRun.matched;
      total += e.latestRun.total;
      scored += 1;
    }
    return {
      matched,
      total,
      scored,
      eligible: filtered.filter((e) => e.status !== 'deprecated').length,
      score: total === 0 ? null : matched / total,
    };
  }, [filtered]);

  const scoreAll = useMutation({
    mutationFn: () => api.scoreGoldenDatasetAll(schemaFilter === 'all' ? undefined : schemaFilter),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['golden-dataset'] }),
  });

  if (list.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <Skeleton className="h-40" />
          <Skeleton className="h-40" />
        </div>
      </div>
    );
  }
  if (list.isError || !list.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load golden dataset" message={String(list.error)} />
      </div>
    );
  }

  return (
    <div
      className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup"
      data-testid="golden-dataset-page"
    >
      <PageHero
        tone="violet"
        eyebrow="Quality"
        title="Golden Dataset"
        lead="Curated code samples paired with the claims each analysis should produce. Re-run after a prompt change to catch regressions."
        actions={
          isAdmin ? (
            <>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => scoreAll.mutate()}
                disabled={scoreAll.isPending}
                data-testid="golden-score-all"
              >
                <RefreshCw className={clsx('h-4 w-4', scoreAll.isPending && 'animate-spin')} />
                {scoreAll.isPending ? 'Scoring…' : 'Run all'}
              </Button>
              <Button
                variant="primary"
                size="sm"
                onClick={() => setCreating(true)}
                data-testid="golden-new"
              >
                <Plus className="h-4 w-4" />
                New entry
              </Button>
            </>
          ) : null
        }
      />

      <Card data-testid="golden-aggregate-banner">
        <CardBody className="flex flex-wrap items-center justify-between gap-6">
          <div>
            <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
              Aggregate score
            </p>
            <p className="mt-1 text-display font-semibold text-ink-primary">
              {aggregate.score === null
                ? '—'
                : `${Math.round(aggregate.score * 100)}%`}
            </p>
            <p className="text-body-sm text-ink-secondary">
              {aggregate.scored}/{aggregate.eligible} entries scored ·{' '}
              {aggregate.matched}/{aggregate.total} claims matched
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Filter
              label="Schema"
              value={schemaFilter}
              onChange={setSchemaFilter}
              options={['all', ...schemas]}
            />
          </div>
        </CardBody>
      </Card>

      {scoreAll.isError && (
        <ErrorBlock
          title="Score-all run failed"
          message={
            scoreAll.error instanceof ApiError
              ? scoreAll.error.message
              : String(scoreAll.error)
          }
        />
      )}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2" data-testid="golden-entry-list">
        {filtered.map((entry) => (
          <EntryCard key={entry.id} entry={entry} onOpen={() => setOpenEntryId(entry.entryId)} />
        ))}
        {filtered.length === 0 && (
          <Card>
            <CardBody className="text-center text-ink-secondary">
              No entries match the current filter.
            </CardBody>
          </Card>
        )}
      </div>

      {openEntryId && (
        <EntryDrawer
          entryId={openEntryId}
          isAdmin={isAdmin}
          onClose={() => setOpenEntryId(null)}
          onMutated={() => queryClient.invalidateQueries({ queryKey: ['golden-dataset'] })}
        />
      )}

      {creating && (
        <NewEntryModal
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            queryClient.invalidateQueries({ queryKey: ['golden-dataset'] });
          }}
        />
      )}
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────
// Entry card (list view)
// ─────────────────────────────────────────────────────────────────────
function EntryCard({
  entry,
  onOpen,
}: {
  entry: GoldenDatasetSummary;
  onOpen: () => void;
}) {
  return (
    <Card
      interactive
      onClick={onOpen}
      data-testid={`golden-entry-${entry.entryId}`}
    >
      <div className="flex items-start justify-between gap-2 border-b border-border-subtle p-4">
        <div>
          <p className="font-mono text-caption text-ink-tertiary">{entry.entryId}</p>
          <p className="mt-1 text-body-lg font-semibold text-ink-primary">{entry.title}</p>
        </div>
        <ScoreBadge run={entry.latestRun} />
      </div>
      <CardBody className="flex flex-wrap items-center gap-2 text-body-sm text-ink-secondary">
        <Badge tone="neutral">{entry.schemaId}</Badge>
        <Badge tone={difficultyTone(entry.difficulty)}>{entry.difficulty}</Badge>
        <Badge tone="review">{entry.trapCategory}</Badge>
        <span className="text-ink-tertiary">·</span>
        <span>{entry.expectedClaimCount} expected claim{entry.expectedClaimCount === 1 ? '' : 's'}</span>
        {entry.hasCanonicalInputs && (
          <>
            <span className="text-ink-tertiary">·</span>
            <Badge tone="success">runtime-equivalence</Badge>
          </>
        )}
        <span className="text-ink-tertiary">·</span>
        <Badge tone={entry.status === 'approved' ? 'success' : entry.status === 'deprecated' ? 'superseded' : 'neutral'}>
          {entry.status}
        </Badge>
      </CardBody>
    </Card>
  );
}

function ScoreBadge({ run }: { run: GoldenDatasetSummary['latestRun'] }) {
  if (!run) return <Badge tone="neutral">not scored</Badge>;
  const pct = Math.round(run.score * 100);
  const tone: 'success' | 'scaffolded' | 'failed' = pct >= 80 ? 'success' : pct >= 50 ? 'scaffolded' : 'failed';
  return (
    <Badge tone={tone}>
      {pct}% · {run.matched}/{run.total}
    </Badge>
  );
}

// ─────────────────────────────────────────────────────────────────────
// Entry detail drawer (modal-style overlay; edit + score + delete)
// ─────────────────────────────────────────────────────────────────────
function EntryDrawer({
  entryId,
  isAdmin,
  onClose,
  onMutated,
}: {
  entryId: string;
  isAdmin: boolean;
  onClose: () => void;
  onMutated: () => void;
}) {
  const detail = useQuery({
    queryKey: ['golden-dataset', entryId],
    queryFn: () => api.getGoldenDatasetEntry(entryId),
  });
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<GoldenDatasetEntry | null>(null);
  const [scoreError, setScoreError] = useState<string | null>(null);

  const score = useMutation({
    mutationFn: () => api.scoreGoldenDatasetEntry(entryId),
    onSuccess: () => {
      setScoreError(null);
      onMutated();
      detail.refetch();
    },
    onError: (err) => setScoreError(err instanceof ApiError ? err.message : String(err)),
  });

  const update = useMutation({
    mutationFn: (body: GoldenDatasetUpsert) => api.updateGoldenDatasetEntry(entryId, body),
    onSuccess: () => {
      setEditing(false);
      onMutated();
      detail.refetch();
    },
  });

  const del = useMutation({
    mutationFn: () => api.deleteGoldenDatasetEntry(entryId),
    onSuccess: () => {
      onMutated();
      onClose();
    },
  });

  return (
    <div
      className="fixed inset-0 z-50 flex items-stretch justify-end bg-black/40 p-0"
      role="dialog"
      data-testid="golden-entry-drawer"
      onClick={onClose}
    >
      <div
        className="flex h-full w-full max-w-2xl flex-col overflow-y-auto bg-surface-primary shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="sticky top-0 z-10 flex items-center justify-between border-b border-line bg-surface-primary p-4">
          <h2 className="font-mono text-body-sm text-ink-secondary">{entryId}</h2>
          <button
            className="rounded p-1 hover:bg-surface-secondary"
            onClick={onClose}
            aria-label="Close"
          >
            <X className="h-4 w-4" />
          </button>
        </header>

        {detail.isPending && (
          <div className="space-y-3 p-4">
            <Skeleton className="h-6 w-64" />
            <Skeleton className="h-32" />
            <Skeleton className="h-24" />
          </div>
        )}
        {detail.isError && (
          <ErrorBlock
            title="Could not load entry"
            message={String(detail.error)}
          />
        )}
        {detail.data && !editing && (
          <div className="space-y-6 p-4">
            <div>
              <h1 className="text-headline font-semibold text-ink-primary">{detail.data.title}</h1>
              <div className="mt-2 flex flex-wrap items-center gap-2 text-body-sm">
                <Badge tone="neutral">{detail.data.schemaId}</Badge>
                <Badge tone={difficultyTone(detail.data.difficulty)}>{detail.data.difficulty}</Badge>
                <Badge tone="review">{detail.data.trapCategory}</Badge>
                <Badge tone={detail.data.status === 'approved' ? 'success' : 'neutral'}>
                  {detail.data.status}
                </Badge>
              </div>
            </div>

            {isAdmin && (
              <div className="flex flex-wrap gap-2">
                <Button
                  size="sm"
                  variant="primary"
                  onClick={() => score.mutate()}
                  disabled={score.isPending}
                  data-testid="golden-score-entry"
                >
                  <Play className={clsx('h-4 w-4', score.isPending && 'animate-pulse')} />
                  {score.isPending ? 'Scoring…' : 'Run scorer'}
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    setDraft(detail.data);
                    setEditing(true);
                  }}
                  data-testid="golden-edit-entry"
                >
                  <Pencil className="h-4 w-4" /> Edit
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    if (confirm(`Delete entry "${entryId}"? This cannot be undone.`)) {
                      del.mutate();
                    }
                  }}
                  data-testid="golden-delete-entry"
                >
                  <Trash2 className="h-4 w-4" /> Delete
                </Button>
              </div>
            )}
            {scoreError && <ErrorBlock title="Scorer failed" message={scoreError} />}

            <Card>
              <div className="border-b border-border-subtle p-4">
                <p className="font-mono text-caption text-ink-tertiary">
                  Source · {detail.data.sourcePath} ({detail.data.sourceLines})
                </p>
              </div>
              <CardBody>
                <pre className="overflow-x-auto rounded bg-sunken p-3 font-mono text-body-sm">
                  {detail.data.sourceContent}
                </pre>
              </CardBody>
            </Card>

            <Card>
              <div className="border-b border-border-subtle p-4">
                <p className="font-mono text-caption text-ink-tertiary">
                  Expected claims ({detail.data.expectedClaims.length})
                </p>
              </div>
              <CardBody className="space-y-2">
                {detail.data.expectedClaims.map((c) => (
                  <div key={c.id} className="rounded border border-border-subtle p-2">
                    <div className="flex items-center gap-2 text-body-sm">
                      <Badge tone="neutral">{c.kind}</Badge>
                      <span className="font-mono text-ink-tertiary">{c.id}</span>
                    </div>
                    <p className="mt-1 break-all font-mono text-caption text-ink-secondary">
                      {c.pattern}
                    </p>
                  </div>
                ))}
              </CardBody>
            </Card>

            {detail.data.notes && (
              <Card>
                <div className="border-b border-border-subtle p-4">
                  <p className="font-mono text-caption text-ink-tertiary">Notes</p>
                </div>
                <CardBody>
                  <p className="whitespace-pre-wrap text-body-sm text-ink-secondary">
                    {detail.data.notes}
                  </p>
                </CardBody>
              </Card>
            )}
          </div>
        )}

        {detail.data && editing && draft && (
          <EditForm
            initial={draft}
            onCancel={() => setEditing(false)}
            onSave={(body) => update.mutate(body)}
            saving={update.isPending}
          />
        )}
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────
// Edit + Create forms (share the EditForm body)
// ─────────────────────────────────────────────────────────────────────
function EditForm({
  initial,
  onCancel,
  onSave,
  saving,
}: {
  initial: GoldenDatasetEntry;
  onCancel: () => void;
  onSave: (body: GoldenDatasetUpsert) => void;
  saving: boolean;
}) {
  const [title, setTitle] = useState(initial.title);
  const [schemaId, setSchemaId] = useState(initial.schemaId);
  const [trapCategory, setTrapCategory] = useState(initial.trapCategory);
  const [difficulty, setDifficulty] = useState(initial.difficulty);
  const [sourcePath, setSourcePath] = useState(initial.sourcePath);
  const [sourceLines, setSourceLines] = useState(initial.sourceLines);
  const [sourceContent, setSourceContent] = useState(initial.sourceContent);
  const [notes, setNotes] = useState(initial.notes);
  const [status, setStatus] = useState(initial.status);
  const [claimsJson, setClaimsJson] = useState(JSON.stringify(initial.expectedClaims, null, 2));
  const [claimsErr, setClaimsErr] = useState<string | null>(null);

  function submit() {
    let parsedClaims: GoldenDatasetExpectedClaim[] | undefined;
    try {
      parsedClaims = JSON.parse(claimsJson);
      setClaimsErr(null);
    } catch (e) {
      setClaimsErr(`Invalid JSON: ${String(e)}`);
      return;
    }
    onSave({
      title,
      schemaId,
      trapCategory,
      difficulty,
      sourcePath,
      sourceLines,
      sourceContent,
      notes,
      status,
      expectedClaims: parsedClaims,
    });
  }

  return (
    <div className="space-y-4 p-4" data-testid="golden-edit-form">
      <Field label="Title" value={title} onChange={setTitle} />
      <div className="grid grid-cols-2 gap-3">
        <Field label="Schema id" value={schemaId} onChange={setSchemaId} />
        <Field label="Trap category" value={trapCategory} onChange={setTrapCategory} />
        <Field label="Difficulty" value={difficulty} onChange={setDifficulty} />
        <Field label="Status" value={status} onChange={setStatus} />
        <Field label="Source path" value={sourcePath} onChange={setSourcePath} />
        <Field label="Source lines" value={sourceLines} onChange={setSourceLines} />
      </div>
      <TextArea label="Source content" value={sourceContent} onChange={setSourceContent} rows={10} mono />
      <TextArea label="Expected claims (JSON)" value={claimsJson} onChange={setClaimsJson} rows={10} mono />
      {claimsErr && <ErrorBlock title="Claims JSON invalid" message={claimsErr} />}
      <TextArea label="Notes" value={notes} onChange={setNotes} rows={4} />
      <div className="flex gap-2">
        <Button variant="primary" size="sm" onClick={submit} disabled={saving}>
          <Save className="h-4 w-4" />
          {saving ? 'Saving…' : 'Save'}
        </Button>
        <Button variant="ghost" size="sm" onClick={onCancel}>Cancel</Button>
      </div>
    </div>
  );
}

function NewEntryModal({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: () => void;
}) {
  const create = useMutation({
    mutationFn: (body: GoldenDatasetUpsert) => api.createGoldenDatasetEntry(body),
    onSuccess: () => onCreated(),
  });
  const [entryId, setEntryId] = useState('');
  const [schemaId, setSchemaId] = useState('cobol');
  const [title, setTitle] = useState('');
  const [trapCategory, setTrapCategory] = useState('');
  const [difficulty, setDifficulty] = useState('medium');
  const [status, setStatus] = useState('draft');
  const [sourcePath, setSourcePath] = useState('');
  const [sourceLines, setSourceLines] = useState('');
  const [sourceContent, setSourceContent] = useState('');
  const [notes, setNotes] = useState('');
  const [claimsJson, setClaimsJson] = useState(
    JSON.stringify(
      [{ kind: 'invariant', id: 'INV-1', pattern: '(?i)...' }],
      null,
      2,
    ),
  );
  const [err, setErr] = useState<string | null>(null);

  function submit() {
    setErr(null);
    let parsedClaims: GoldenDatasetExpectedClaim[];
    try {
      parsedClaims = JSON.parse(claimsJson);
    } catch (e) {
      setErr(`Invalid claims JSON: ${String(e)}`);
      return;
    }
    create.mutate(
      {
        entryId,
        schemaId,
        title,
        trapCategory,
        difficulty,
        status,
        sourcePath,
        sourceLines,
        sourceContent,
        notes,
        expectedClaims: parsedClaims,
      },
      {
        onError: (e) =>
          setErr(e instanceof ApiError ? e.message : String(e)),
      },
    );
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      role="dialog"
      data-testid="golden-new-modal"
      onClick={onClose}
    >
      <div
        className="w-full max-w-2xl space-y-4 overflow-y-auto rounded bg-surface-primary p-4 shadow-xl"
        style={{ maxHeight: '90vh' }}
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-center justify-between">
          <h2 className="text-headline font-semibold text-ink-primary">New Golden Dataset entry</h2>
          <button className="rounded p-1 hover:bg-surface-secondary" onClick={onClose} aria-label="Close">
            <X className="h-4 w-4" />
          </button>
        </header>
        <Field label="Entry id (unique, kebab-case)" value={entryId} onChange={setEntryId} />
        <div className="grid grid-cols-2 gap-3">
          <Field label="Schema id" value={schemaId} onChange={setSchemaId} />
          <Field label="Trap category" value={trapCategory} onChange={setTrapCategory} />
          <Field label="Difficulty" value={difficulty} onChange={setDifficulty} />
          <Field label="Status" value={status} onChange={setStatus} />
          <Field label="Source path" value={sourcePath} onChange={setSourcePath} />
          <Field label="Source lines" value={sourceLines} onChange={setSourceLines} />
        </div>
        <Field label="Title" value={title} onChange={setTitle} />
        <TextArea label="Source content" value={sourceContent} onChange={setSourceContent} rows={8} mono />
        <TextArea label="Expected claims (JSON)" value={claimsJson} onChange={setClaimsJson} rows={8} mono />
        <TextArea label="Notes" value={notes} onChange={setNotes} rows={3} />
        {err && <ErrorBlock title="Could not create entry" message={err} />}
        <div className="flex gap-2">
          <Button variant="primary" size="sm" onClick={submit} disabled={create.isPending}>
            <Save className="h-4 w-4" />
            {create.isPending ? 'Creating…' : 'Create'}
          </Button>
          <Button variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
        </div>
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────
// Tiny helpers (kept in-file so this page is self-contained)
// ─────────────────────────────────────────────────────────────────────

function Field({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <label className="block">
      <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
        {label}
      </span>
      <input
        className="mt-1 w-full rounded border border-line bg-surface-primary px-2 py-1 text-body-sm"
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  );
}

function TextArea({
  label,
  value,
  onChange,
  rows,
  mono,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  rows: number;
  mono?: boolean;
}) {
  return (
    <label className="block">
      <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
        {label}
      </span>
      <textarea
        className={clsx(
          'mt-1 w-full rounded border border-line bg-surface-primary px-2 py-1 text-body-sm',
          mono && 'font-mono',
        )}
        rows={rows}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
    </label>
  );
}

function Filter({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: string[];
}) {
  return (
    <label className="flex items-center gap-2 text-body-sm">
      <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
        {label}
      </span>
      <select
        className="rounded border border-line bg-surface-primary px-2 py-1"
        value={value}
        onChange={(e) => onChange(e.target.value)}
      >
        {options.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </select>
    </label>
  );
}

function difficultyTone(d: string): 'success' | 'scaffolded' | 'failed' | 'neutral' {
  if (d === 'easy') return 'success';
  if (d === 'medium') return 'scaffolded';
  if (d === 'hard') return 'failed';
  return 'neutral';
}

function uniq<T>(xs: T[]): T[] {
  return Array.from(new Set(xs));
}
