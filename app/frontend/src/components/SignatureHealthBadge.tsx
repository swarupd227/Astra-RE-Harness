import { useQuery } from '@tanstack/react-query';
import { ShieldCheck, ShieldAlert, Loader2 } from 'lucide-react';
import { api } from '@/lib/api';
import { Badge } from '@/components/Badge';

/**
 * Signature health badge — value-add #8 in the Nous platform pitch.
 *
 * Mounted inline next to any signed-spec context. Shows:
 *   - green "Signed · healthy"  when the corpus has not been re-ingested
 *     since signing.
 *   - amber "Signed · drift {n}d" when a newer SourceVersion exists.
 *   - nothing for unsigned specs.
 *
 * Uses GET /api/v1/specs/{id}/signature-health for the verdict so the
 * badge is correct without the page needing to know about manifest hashes.
 */
export function SignatureHealthBadge({ specId, compact = false }: { specId: string; compact?: boolean }) {
  const q = useQuery({
    queryKey: ['signature-health', specId],
    queryFn: () => api.getSignatureHealth(specId),
    staleTime: 30_000,
  });

  if (q.isPending) {
    return (
      <Badge tone="neutral">
        <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
        Signature
      </Badge>
    );
  }
  if (q.isError || !q.data) return null;
  if (q.data.state === 'unsigned') return null;

  if (q.data.state === 'healthy') {
    return (
      <Badge tone="signed" data-testid={`signature-health-${specId}`}>
        <ShieldCheck className="h-3 w-3" aria-hidden="true" />
        {compact ? 'Healthy' : 'Signature · healthy'}
      </Badge>
    );
  }

  // drift
  const ageDays = q.data.driftAgeDays ?? 0;
  return (
    <Badge tone="failed" data-testid={`signature-health-${specId}`}>
      <ShieldAlert className="h-3 w-3" aria-hidden="true" />
      {compact ? `Drift ${ageDays}d` : `Signature drift · ${ageDays}d`}
    </Badge>
  );
}
