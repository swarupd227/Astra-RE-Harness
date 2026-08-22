import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  CheckCircle2, KeyRound, Loader2, PlugZap, RotateCcw, Save, ShieldCheck, XCircle,
} from 'lucide-react';
import { api, ApiError, type LlmTestResult } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Task #178 — LLM provider settings: set the Anthropic API key from the UI
 * and prove it works with a live Test Connection call.
 *
 * The key is write-only: this page sends it once and the API only ever
 * returns a masked hint (e.g. "sk-ant-…AA"). A key saved here is stored in
 * platform config and applied to the running process immediately; if the
 * API booted in mock fallback (no key at startup), the status endpoint
 * reports that a restart is needed and this page surfaces it honestly.
 */
export function LlmSettingsPage() {
  const qc = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  const settings = useQuery({ queryKey: ['llm-settings'], queryFn: api.getLlmSettings });

  const [draftKey, setDraftKey] = useState('');
  const [testResult, setTestResult] = useState<LlmTestResult | null>(null);

  const invalidate = () => qc.invalidateQueries({ queryKey: ['llm-settings'] });

  const saveKey = useMutation({
    mutationFn: (key: string) => api.setLlmKey(key),
    onSuccess: () => {
      setDraftKey('');
      setTestResult(null);
      invalidate();
    },
  });
  const clearKey = useMutation({
    mutationFn: api.clearLlmKey,
    onSuccess: () => {
      setTestResult(null);
      invalidate();
    },
  });
  const test = useMutation({
    mutationFn: api.testLlmConnection,
    onSuccess: (r) => setTestResult(r),
    onError: () => setTestResult(null),
  });

  if (settings.isPending) {
    return (
      <div className="mx-auto max-w-[900px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }
  if (settings.isError || !settings.data) {
    return (
      <div className="mx-auto max-w-[900px] p-6 lg:p-10">
        <ErrorBlock title="Could not load LLM settings" message={String(settings.error)} />
      </div>
    );
  }

  const s = settings.data;
  const mutationError =
    (saveKey.error as ApiError | null) ??
    (clearKey.error as ApiError | null) ??
    (test.error as ApiError | null);

  return (
    <div className="mx-auto max-w-[900px] space-y-6 p-6 lg:p-10 fadeup" data-testid="llm-settings-page">
      <header>
        <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
          Task #178 · Provider configuration
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">LLM Provider</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          The Anthropic API key used for extraction, generation, and documentation.
          The key is write-only — once saved, the platform only ever shows a masked hint.
        </p>
      </header>

      {/* ── Current status ─────────────────────────────────────────── */}
      <Card>
        <CardHeader
          title={<span className="flex items-center gap-2"><ShieldCheck className="h-4 w-4 text-ink-tertiary" />Current configuration</span>}
        />
        <CardBody>
          <dl className="grid grid-cols-1 gap-x-8 gap-y-3 sm:grid-cols-2">
            <div>
              <dt className="text-caption uppercase tracking-wider text-ink-tertiary">Provider</dt>
              <dd className="mt-0.5 flex items-center gap-2 font-mono text-sm text-ink-primary">
                {s.configuredProvider}
                {s.activeProvider === s.configuredProvider
                  ? <Badge tone="success">active</Badge>
                  : <Badge tone="review">running as {s.activeProvider}</Badge>}
              </dd>
            </div>
            <div>
              <dt className="text-caption uppercase tracking-wider text-ink-tertiary">Model</dt>
              <dd className="mt-0.5 font-mono text-sm text-ink-primary">{s.model}</dd>
            </div>
            <div>
              <dt className="text-caption uppercase tracking-wider text-ink-tertiary">Endpoint</dt>
              <dd className="mt-0.5 font-mono text-sm text-ink-primary">{s.baseUrl}</dd>
            </div>
            <div>
              <dt className="text-caption uppercase tracking-wider text-ink-tertiary">API key</dt>
              <dd className="mt-0.5 flex items-center gap-2 font-mono text-sm text-ink-primary">
                {s.keyConfigured ? (
                  <>
                    <span>{s.keyHint || 'configured'}</span>
                    <Badge tone={s.keySource === 'database' ? 'info' : 'neutral'}>
                      {s.keySource === 'database' ? 'set from UI' : 'from environment'}
                    </Badge>
                  </>
                ) : (
                  <Badge tone="failed">not configured</Badge>
                )}
              </dd>
            </div>
          </dl>

          {s.requiresRestart && (
            <p className="mt-4 rounded border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm text-amber-700 dark:text-amber-400">
              A key is saved, but the API started without one and is running the mock
              provider. Restart the API service to activate real Claude calls.
            </p>
          )}
        </CardBody>
      </Card>

      {/* ── Set key ────────────────────────────────────────────────── */}
      <Card>
        <CardHeader title={<span className="flex items-center gap-2"><KeyRound className="h-4 w-4 text-ink-tertiary" />Set API key</span>} />
        <CardBody>
          {!isAdmin ? (
            <p className="text-sm text-ink-tertiary">
              Only the Admin persona can change the API key. Switch persona to make changes.
            </p>
          ) : (
            <div className="space-y-3">
              <div className="flex flex-col gap-2 sm:flex-row">
                <input
                  type="password"
                  autoComplete="off"
                  value={draftKey}
                  onChange={(e) => setDraftKey(e.target.value)}
                  placeholder="sk-ant-…"
                  aria-label="Anthropic API key"
                  className="min-w-0 flex-1 rounded border border-border-subtle bg-raised px-3 py-2 font-mono text-sm text-ink-primary placeholder:text-ink-tertiary focus:border-brand focus:outline-none"
                />
                <Button
                  variant="primary"
                  onClick={() => saveKey.mutate(draftKey)}
                  disabled={draftKey.trim().length === 0 || saveKey.isPending}
                >
                  {saveKey.isPending
                    ? <Loader2 className="h-4 w-4 animate-spin" />
                    : <Save className="h-4 w-4" />}
                  Save key
                </Button>
              </div>
              <p className="text-xs text-ink-tertiary">
                Paste a key from the Anthropic Console. It is stored server-side and never
                displayed again — only the masked hint above.
              </p>
              {s.keySource === 'database' && (
                <Button
                  variant="secondary"
                  onClick={() => clearKey.mutate()}
                  disabled={clearKey.isPending}
                >
                  {clearKey.isPending
                    ? <Loader2 className="h-4 w-4 animate-spin" />
                    : <RotateCcw className="h-4 w-4" />}
                  Remove UI-set key (revert to environment)
                </Button>
              )}
            </div>
          )}
        </CardBody>
      </Card>

      {/* ── Test connection ────────────────────────────────────────── */}
      <Card>
        <CardHeader title={<span className="flex items-center gap-2"><PlugZap className="h-4 w-4 text-ink-tertiary" />Test connection</span>} />
        <CardBody>
          <div className="space-y-3">
            <p className="text-sm text-ink-secondary">
              Makes a live, zero-cost call to the Anthropic models endpoint with the
              currently effective key.
            </p>
            <Button
              variant="secondary"
              onClick={() => test.mutate()}
              disabled={!isAdmin || !s.keyConfigured || test.isPending}
              title={!isAdmin ? 'Admin only' : !s.keyConfigured ? 'No key configured' : undefined}
            >
              {test.isPending
                ? <Loader2 className="h-4 w-4 animate-spin" />
                : <PlugZap className="h-4 w-4" />}
              Test connection
            </Button>

            {testResult && (
              <div
                className={`flex items-start gap-2 rounded border px-3 py-2 text-sm ${
                  testResult.ok
                    ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400'
                    : 'border-rose-500/40 bg-rose-500/10 text-rose-700 dark:text-rose-400'
                }`}
                data-testid="llm-test-result"
              >
                {testResult.ok
                  ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />
                  : <XCircle className="mt-0.5 h-4 w-4 shrink-0" />}
                <div>
                  {testResult.ok ? (
                    <>
                      <p className="font-medium">
                        Connected to {testResult.endpoint} in {testResult.latencyMs} ms.
                      </p>
                      {testResult.models && testResult.models.length > 0 && (
                        <p className="mt-0.5 font-mono text-xs opacity-80">
                          models: {testResult.models.join(', ')}
                        </p>
                      )}
                    </>
                  ) : (
                    <p className="font-medium">{testResult.error}</p>
                  )}
                </div>
              </div>
            )}
          </div>
        </CardBody>
      </Card>

      {mutationError && (
        <ErrorBlock title="Request failed" message={mutationError.message} />
      )}
    </div>
  );
}
