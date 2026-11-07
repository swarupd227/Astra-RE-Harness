import { DefaultEvidenceBody, textOf, type EvidenceBodyProps } from '@/evidence/views/DefaultBody';

/**
 * A setting: the name the application reads it by and its value, large; the file around it with the
 * line marked. A value that looks like a secret is not kept in the evidence and is hidden in the lines
 * shown (the server redacts them), and the card says so.
 */
export function ConfigView({ detail }: EvidenceBodyProps) {
  const s = detail.structured ?? {};
  const key = textOf(s.key) || detail.label;
  const list = Array.isArray(s.values) ? (s.values as unknown[]).map(textOf) : null;
  const value = list ? `[${list.join(', ')}]` : textOf(s.value);
  const masked = s.masked === true;

  return (
    <div className="space-y-3" data-testid="config-view">
      <div className="rounded-md border border-line-subtle bg-sunken px-3 py-2">
        <p className="break-words font-mono text-micro text-ink-tertiary" data-testid="config-key">{key}</p>
        <p className="mt-1 break-words font-mono text-body font-medium text-ink-primary" data-testid="config-value">
          {value === '' ? <span className="text-ink-tertiary">(empty)</span> : value}
        </p>
        {masked && (
          <p className="mt-1 text-micro text-ink-tertiary" data-testid="config-masked">
            This value looks like a secret, so it is hidden here and the evidence does not keep it.
          </p>
        )}
      </div>
      <DefaultEvidenceBody detail={detail} />
    </div>
  );
}
