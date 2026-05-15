import { useQuery } from '@tanstack/react-query';
import { ShieldCheck, ShieldAlert, Server, Cpu, FileText } from 'lucide-react';
import { api } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';

/**
 * Provider Settings card — value-add #1 in the Nous platform pitch.
 *
 * Promotes the residency/cost flags that used to live in a single-line
 * audit-trail debug strip into a visible card with colour-coded trust
 * chips. Rendered on every page where a user is reasoning about an LLM
 * artifact (Spec, Scaffold, Validation) so the SOC2 / SOX reviewer can
 * see "ZDR · no-train · no-retention" at a glance.
 *
 * Use the `compact` variant in tight headers; the default variant is the
 * full card.
 */
export function ProviderSettingsCard({ compact = false }: { compact?: boolean } = {}) {
  const q = useQuery({
    queryKey: ['providerSettings'],
    queryFn: api.getProviderSettings,
    staleTime: 5 * 60_000,
  });

  if (q.isPending) {
    return <Skeleton className={compact ? 'h-8 w-full' : 'h-32 w-full'} />;
  }
  if (q.isError || !q.data) return null;

  const { provider, residency, promptLibrary } = q.data;
  const trustChips = buildTrustChips(residency);

  if (compact) {
    return (
      <div
        data-testid="provider-settings-card"
        className="flex flex-wrap items-center gap-2 rounded-md border border-border-subtle bg-sunken/60 px-3 py-2"
      >
        <span className="inline-flex items-center gap-1.5 text-caption font-medium text-ink-secondary">
          <Cpu className="h-3.5 w-3.5" aria-hidden="true" />
          {provider.displayName}
          <span className="text-ink-tertiary">·</span>
          <span className="font-mono text-[11px]">{provider.model}</span>
        </span>
        {trustChips.map((c) => (
          <Badge key={c.label} tone={c.tone}>
            {c.icon}
            {c.label}
          </Badge>
        ))}
      </div>
    );
  }

  return (
    <Card data-testid="provider-settings-card">
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <Cpu className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
            AI provider · {provider.displayName}
          </span>
        }
        description={
          <span className="font-mono text-[12px] text-ink-secondary">
            {provider.model}
            {provider.apiVersion && (
              <>
                <span className="mx-1.5 text-ink-tertiary">·</span>
                api {provider.apiVersion}
              </>
            )}
          </span>
        }
      />
      <CardBody className="space-y-4">
        <div>
          <div className="text-caption uppercase tracking-wide text-ink-tertiary">
            Data residency &amp; training posture
          </div>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            {trustChips.map((c) => (
              <Badge key={c.label} tone={c.tone}>
                {c.icon}
                {c.label}
              </Badge>
            ))}
          </div>
          <div className="mt-2 font-mono text-[11px] text-ink-tertiary">
            {residency.configVersion}
          </div>
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          {provider.endpointHostname && (
            <div>
              <div className="text-caption uppercase tracking-wide text-ink-tertiary">
                <span className="inline-flex items-center gap-1.5">
                  <Server className="h-3 w-3" aria-hidden="true" />
                  Endpoint
                </span>
              </div>
              <div className="mt-1 font-mono text-[12px] text-ink-primary">
                {provider.endpointHostname}
              </div>
            </div>
          )}
          {promptLibrary.extractPromptId && (
            <div>
              <div className="text-caption uppercase tracking-wide text-ink-tertiary">
                <span className="inline-flex items-center gap-1.5">
                  <FileText className="h-3 w-3" aria-hidden="true" />
                  Prompt template
                </span>
              </div>
              <div className="mt-1 font-mono text-[12px] text-ink-primary">
                {promptLibrary.extractPromptId}@{promptLibrary.extractPromptVersion}
              </div>
              <div className="font-mono text-[11px] text-ink-tertiary">
                {promptLibrary.schemaId} → {promptLibrary.targetStack}
              </div>
            </div>
          )}
        </div>
      </CardBody>
    </Card>
  );
}

type ChipTone = Parameters<typeof Badge>[0]['tone'];
type Chip = { label: string; tone: ChipTone; icon?: JSX.Element };

function buildTrustChips(r: {
  zdr: boolean;
  noTraining: boolean;
  noRetention: boolean;
  enterpriseEndpoint: boolean;
  offline: boolean;
}): Chip[] {
  // Offline mode (mock provider): show a single neutral chip so the
  // dashboard doesn't claim trust signals that don't apply.
  if (r.offline) {
    return [
      {
        label: 'Offline · no network',
        tone: 'neutral',
        icon: <ShieldAlert className="h-3 w-3" aria-hidden="true" />,
      },
    ];
  }

  const chip = (label: string, on: boolean): Chip => ({
    label,
    tone: on ? 'success' : 'failed',
    icon: on ? (
      <ShieldCheck className="h-3 w-3" aria-hidden="true" />
    ) : (
      <ShieldAlert className="h-3 w-3" aria-hidden="true" />
    ),
  });

  return [
    chip('ZDR', r.zdr),
    chip('No training', r.noTraining),
    chip('No retention', r.noRetention),
    chip('Enterprise endpoint', r.enterpriseEndpoint),
  ];
}
