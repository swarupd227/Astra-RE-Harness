import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  AlertTriangle,
  ChevronDown,
  Copy,
  Database,
  FileCode,
  GitBranch,
  Layers,
  Link2,
  ShieldCheck,
  Sparkles,
  UserCheck,
} from 'lucide-react';
import { api, type EvidenceResponse } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { VerifySignatureModal } from '@/components/VerifySignatureModal';

/**
 * Evidence Trail — the full chain of provenance for a spec, from source to
 * scaffold. Designed to be the answer to the question "how do you know this
 * is faithful to the original?". Each block surfaces verifiable facts
 * (hashes, model+version, key ids, line ranges) instead of marketing
 * adjectives.
 *
 * Honest about the dev-mode posture:
 *   - Signature block explicitly flags software RSA + dev key id
 *   - Author block carries the persona name so an auditor sees "Dev User (SME)"
 *     not "John Doe, NZ-registered SME"
 *
 * Loads in one round-trip via /api/v1/specs/{specId}; the verify modal is
 * lazy and only renders when the user clicks.
 */
export function EvidenceTrail({ specId }: { specId: string }) {
  const evidence = useQuery({
    queryKey: ['evidence', specId],
    queryFn: () => api.getSpec(specId),
    refetchInterval: 30_000,
  });
  const [verifyOpen, setVerifyOpen] = useState(false);

  if (evidence.isPending) {
    return <Skeleton className="h-[480px] w-full" />;
  }
  if (evidence.isError) {
    return <ErrorBlock title="Could not load evidence trail" message={evidence.error.message} onRetry={() => evidence.refetch()} />;
  }
  const e: EvidenceResponse = evidence.data;

  return (
    <section
      className="rounded-md border border-border-subtle bg-raised"
      aria-label="Evidence trail"
      data-testid="evidence-trail"
    >
      <header className="border-b border-border-subtle px-5 py-3">
        <div className="flex items-center gap-2">
          <Layers className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
          <h2 className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
            Evidence trail · {e.subroutine?.name ?? 'spec'}
          </h2>
        </div>
        <p className="mt-1 text-caption text-ink-secondary">
          Every fact below is independently verifiable. Hashes are copy-able; the signature is
          checkable with the public key on any machine.
        </p>
      </header>

      <div className="space-y-2 p-5">
        <SourceBlock e={e} />
        <Arrow label="parsed to a syntax tree" />
        <AstBlock e={e} />
        <Arrow label="LLM call to provider" />
        <ProviderBlock e={e} />
        <Arrow label="per-claim SME review" />
        <ReviewBlock e={e} />
        <Arrow label="RFC-8785 canonical-JSON signature" />
        <SignatureBlock e={e} onVerify={() => setVerifyOpen(true)} />
        {e.scaffold && (
          <>
            <Arrow label="scaffold generated from signed spec" />
            <ScaffoldBlock e={e} />
          </>
        )}
      </div>

      {e.signature && (
        <VerifySignatureModal
          open={verifyOpen}
          onClose={() => setVerifyOpen(false)}
          specId={e.id}
          signature={e.signature}
        />
      )}
    </section>
  );
}

// ─── Blocks ──────────────────────────────────────────────────────────

function SourceBlock({ e }: { e: EvidenceResponse }) {
  const file = e.subroutine?.file;
  const corpus = e.subroutine?.corpus;
  const version = e.subroutine?.sourceVersion;
  if (!file || !corpus || !e.subroutine) return <BlockMissing label="Source" />;

  const linesSpan = `L${e.subroutine.lineStart}–L${e.subroutine.lineEnd}`;
  return (
    <Block icon={FileCode} label="Source" tone="neutral">
      <Row label="Routine" mono value={e.subroutine.name} />
      <Row label="Signature" value={e.subroutine.signature} />
      <Row label="File" mono value={`${file.relativePath} · ${file.lineCount} LOC`} />
      <Row label="Span" mono value={linesSpan} />
      <HashRow label="File hash" value={file.fileHash} />
      <Row
        label="Project"
        value={
          <span className="inline-flex items-center gap-2">
            <Database className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
            <span className="font-medium text-ink-primary">{corpus.name}</span>
            <span className="font-mono text-caption text-ink-tertiary">· {corpus.sourceType}</span>
            {version?.gitCommitHash && (
              <span className="font-mono text-caption text-ink-tertiary">
                · commit {version.gitCommitHash.slice(0, 8)}
              </span>
            )}
          </span>
        }
      />
    </Block>
  );
}

function AstBlock({ e }: { e: EvidenceResponse }) {
  const commons = e.subroutine?.commonBlockRefs ?? [];
  const calls = e.subroutine?.calledSubroutines ?? [];
  return (
    <Block icon={Layers} label="AST" tone="neutral">
      <Row
        label="Cross-references"
        value={
          <span className="font-mono text-caption">
            {calls.length > 0 ? `${calls.length} CALL` : '0 CALL'} ·{' '}
            {commons.length > 0 ? `${commons.length} COMMON` : '0 COMMON'}
          </span>
        }
      />
      {calls.length > 0 && (
        <Row
          label="Calls"
          value={
            <div className="flex flex-wrap gap-1">
              {calls.map((c) => (
                <span key={c} className="rounded-sm border border-border-subtle bg-sunken px-1.5 py-0.5 font-mono text-caption text-ink-secondary">
                  {c}
                </span>
              ))}
            </div>
          }
        />
      )}
      {commons.length > 0 && (
        <Row
          label="COMMON blocks"
          value={
            <div className="flex flex-wrap gap-1">
              {commons.map((c) => (
                <span key={c} className="rounded-sm border border-border-subtle bg-sunken px-1.5 py-0.5 font-mono text-caption text-ink-secondary">
                  /{c}/
                </span>
              ))}
            </div>
          }
        />
      )}
    </Block>
  );
}

function ProviderBlock({ e }: { e: EvidenceResponse }) {
  const c = e.llmCall;
  if (!c) return <BlockMissing label="Provider" hint="No LLM call recorded — the spec hasn't been extracted." />;

  // Residency posture lives in the providerConfigVersion stamp.
  const zdr = /zdr=true/i.test(c.providerConfigVersion);
  const noTrain = /no-train/i.test(c.providerConfigVersion);
  const noRet = /no-retention/i.test(c.providerConfigVersion);

  return (
    <Block icon={Sparkles} label="Provider" tone="draft">
      <Row label="Provider" mono value={`${c.provider} · ${c.model}`} />
      <Row label="Prompt template" mono value={`${c.promptTemplateId} @ ${c.promptTemplateVersion}`} />
      <Row
        label="Residency"
        value={
          <div className="flex flex-wrap gap-1.5">
            {zdr && <PostureChip label="zdr=true" ok />}
            {noTrain && <PostureChip label="no-train" ok />}
            {noRet && <PostureChip label="no-retention" ok />}
            <span className="font-mono text-caption text-ink-tertiary" title={c.providerConfigVersion}>
              · {c.providerConfigVersion.replace(/^[a-z]+:/, '')}
            </span>
          </div>
        }
      />
      <Row
        label="Tokens"
        mono
        value={
          <span className="text-ink-primary">
            <span className="font-semibold">{c.inputTokens.toLocaleString()}</span> in /{' '}
            <span className="font-semibold">{c.outputTokens.toLocaleString()}</span> out
          </span>
        }
      />
      <Row label="Latency" mono value={`${(c.latencyMs / 1000).toFixed(1)}s`} />
      <Row label="Cost" mono value={<span className="font-semibold text-status-draft">${c.costUsd.toFixed(4)}</span>} />
      <Row label="Called at" mono value={new Date(c.calledAt).toLocaleString()} />
    </Block>
  );
}

function ReviewBlock({ e }: { e: EvidenceResponse }) {
  const r = e.review;
  if (r.total === 0)
    return <BlockMissing label="Review" hint="No claim reviews yet — the spec hasn't been reviewed by an SME." />;

  const order = ['accept', 'edit', 'reject', 'question'] as const;
  return (
    <Block icon={UserCheck} label="Review" tone="review">
      <Row
        label="Decisions"
        value={
          <div className="flex flex-wrap gap-1.5">
            {order.map((k) => {
              const n = r.byAction[k] ?? 0;
              if (n === 0) return null;
              return (
                <span
                  key={k}
                  className="rounded-sm border border-border-subtle bg-sunken px-1.5 py-0.5 text-caption font-medium uppercase tracking-wider text-ink-secondary"
                >
                  <span className="font-semibold text-ink-primary">{n}</span> {k}
                </span>
              );
            })}
            <span className="font-mono text-caption text-ink-tertiary">· total {r.total}</span>
          </div>
        }
      />
    </Block>
  );
}

function SignatureBlock({ e, onVerify }: { e: EvidenceResponse; onVerify: () => void }) {
  const sig = e.signature;
  if (!sig)
    return <BlockMissing label="Signature" hint="Not yet signed — the SME has not signed off on this spec." />;

  return (
    <Block icon={ShieldCheck} label="Signature" tone="signed">
      <Row label="Signed by" value={<span className="font-medium text-ink-primary">{sig.signerDisplay}</span>} />
      <Row label="Signed at" mono value={new Date(sig.signedAt).toLocaleString()} />
      <Row label="Algorithm" mono value={sig.algorithm} />
      <Row label="Key id" mono value={sig.keyId} />
      <HashRow label="Canonical hash" value={sig.specCanonicalHash} />
      <HashRow label="Source hash" value={sig.sourceVersionHash} />
      <HashRow label="Signature" value={sig.signatureBase64} truncate={32} />

      <div className="mt-2 rounded-sm border border-status-scaffolded/40 bg-[#FBF1D9]/40 px-2.5 py-1.5">
        <div className="flex items-start gap-1.5 text-caption">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-status-scaffolded" aria-hidden="true" />
          <p className="text-ink-secondary">
            <span className="font-semibold text-ink-primary">Dev posture:</span> software RSA in
            Postgres, dev-persona signer. Azure Key Vault Managed HSM + OIDC SME identity land in
            Phase D. The signature is real and verifiable; legal weight comes with PROD wiring.
          </p>
        </div>
      </div>

      <div className="mt-2 flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={onVerify}
          className="inline-flex items-center gap-1.5 rounded-md border border-accent bg-accent/10 px-3 py-1.5 text-caption font-medium text-accent hover:bg-accent/20 focus-visible:outline-2 focus-visible:outline-ink-primary"
          data-testid="verify-signature"
        >
          <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" /> Verify signature
        </button>
        <a
          href={`/api/v1/specs/${e.id}/signed-manifest`}
          download={`signed-${e.subroutine?.name ?? 'spec'}.json`}
          className="inline-flex items-center gap-1.5 rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-caption font-medium text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
          data-testid="download-manifest"
        >
          <Link2 className="h-3.5 w-3.5" aria-hidden="true" /> Download signed.json
        </a>
      </div>
    </Block>
  );
}

function ScaffoldBlock({ e }: { e: EvidenceResponse }) {
  const s = e.scaffold!;
  return (
    <Block icon={GitBranch} label="Scaffold" tone="scaffolded">
      <Row label="Target" mono value={s.targetPlatform} />
      <Row
        label="Files"
        mono
        value={`${s.fileCount} files · ${s.totalLines.toLocaleString()} LOC · ${s.todoCount} TODOs (each links to a claim id)`}
      />
      {s.gitBranch && (
        <Row
          label="Git"
          value={
            <span className="font-mono text-caption">
              branch{' '}
              <span className="text-ink-primary">{s.gitBranch}</span> @{' '}
              <span className="text-ink-primary">{s.gitCommitHash?.slice(0, 12) ?? '?'}</span>
              <span className="ml-1 text-ink-tertiary">
                {s.gitCommitUrl?.startsWith('git://stub') ? '(stub commit)' : ''}
              </span>
            </span>
          }
        />
      )}
      <Row label="Generated at" mono value={new Date(s.generatedAt).toLocaleString()} />
    </Block>
  );
}

// ─── Building blocks ────────────────────────────────────────────────

const toneClass: Record<string, string> = {
  neutral: 'border-border-subtle bg-canvas',
  draft: 'border-status-draft/40 bg-accent-muted/30',
  review: 'border-status-review/40 bg-[#DAEFE9]/30',
  signed: 'border-status-signed/40 bg-[#DCE6F5]/30',
  scaffolded: 'border-status-scaffolded/40 bg-[#FBF1D9]/30',
};

const toneIcon: Record<string, string> = {
  neutral: 'text-ink-secondary',
  draft: 'text-status-draft',
  review: 'text-status-review',
  signed: 'text-status-signed',
  scaffolded: 'text-status-scaffolded',
};

function Block({
  icon: Icon,
  label,
  tone = 'neutral',
  children,
}: {
  icon: typeof Sparkles;
  label: string;
  tone?: keyof typeof toneClass;
  children: React.ReactNode;
}) {
  return (
    <div
      className={`rounded-md border ${toneClass[tone]} p-3.5`}
      data-testid={`evidence-block-${label.toLowerCase()}`}
    >
      <header className="mb-2 flex items-center gap-2">
        <span className={`flex h-6 w-6 items-center justify-center rounded ${toneIcon[tone]}`}>
          <Icon className="h-3.5 w-3.5" aria-hidden="true" />
        </span>
        <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{label}</span>
      </header>
      <dl className="space-y-1">{children}</dl>
    </div>
  );
}

function BlockMissing({ label, hint }: { label: string; hint?: string }) {
  return (
    <div className="rounded-md border border-dashed border-border bg-sunken/40 p-3.5">
      <header className="mb-1 flex items-center gap-2">
        <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{label}</span>
        <span className="font-mono text-caption text-ink-tertiary italic">pending</span>
      </header>
      {hint && <p className="text-caption text-ink-tertiary">{hint}</p>}
    </div>
  );
}

function Row({ label, value, mono }: { label: string; value: React.ReactNode; mono?: boolean }) {
  return (
    <div className="grid grid-cols-[120px_minmax(0,1fr)] items-baseline gap-3">
      <dt className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{label}</dt>
      <dd className={'min-w-0 text-caption text-ink-primary ' + (mono ? 'font-mono' : '')}>{value}</dd>
    </div>
  );
}

function HashRow({ label, value, truncate = 22 }: { label: string; value: string; truncate?: number }) {
  return (
    <div className="grid grid-cols-[120px_minmax(0,1fr)] items-baseline gap-3">
      <dt className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{label}</dt>
      <dd className="flex min-w-0 items-center gap-1.5">
        <code
          className="truncate rounded-sm bg-sunken px-1.5 py-0.5 font-mono text-caption text-ink-primary"
          title={value}
        >
          {value.length > truncate ? value.slice(0, truncate) + '…' : value}
        </code>
        <CopyButton value={value} />
      </dd>
    </div>
  );
}

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(value);
          setCopied(true);
          setTimeout(() => setCopied(false), 1200);
        } catch { /* clipboard denied */ }
      }}
      title="Copy"
      aria-label={copied ? 'Copied' : 'Copy to clipboard'}
      className="shrink-0 rounded p-0.5 text-ink-tertiary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
    >
      <Copy className="h-3 w-3" aria-hidden="true" />
      {copied && <span className="sr-only">Copied</span>}
    </button>
  );
}

function PostureChip({ label, ok }: { label: string; ok?: boolean }) {
  return (
    <span
      className={
        'inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-caption font-medium uppercase tracking-wider ' +
        (ok ? 'bg-status-review/15 text-status-review' : 'bg-sunken text-ink-tertiary')
      }
    >
      {ok && <span aria-hidden="true">●</span>}
      {label}
    </span>
  );
}

function Arrow({ label }: { label: string }) {
  return (
    <div className="flex items-center gap-2 pl-7">
      <ChevronDown className="h-3 w-3 text-ink-tertiary" aria-hidden="true" />
      <span className="font-mono text-caption text-ink-tertiary">↓ {label}</span>
    </div>
  );
}
