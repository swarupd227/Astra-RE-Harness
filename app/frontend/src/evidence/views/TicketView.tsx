import { Badge } from '@/components/Badge';
import { SourceLines, textOf, type EvidenceBodyProps } from '@/evidence/views/DefaultBody';

type Tone = 'failed' | 'draft' | 'neutral';

/** "2" or "P2" → P2; the two top priorities read as failures, the third as a warning. */
function priorityOf(raw: string): { label: string; tone: Tone } {
  const m = /^p?([1-5])\b/i.exec(raw.trim());
  if (!m) return { label: raw, tone: 'neutral' };
  const n = Number(m[1]);
  return { label: `P${n}${raw.trim().length > m[0].length ? raw.trim().slice(m[0].length) : ''}`, tone: n <= 2 ? 'failed' : n === 3 ? 'draft' : 'neutral' };
}

/**
 * A ticket as a record: its number, priority and state up top, then every column of the row as it was
 * exported, the long ones (a description, close notes) given the full width. The row's own line in the
 * export is a click away.
 */
export function TicketView({ detail }: EvidenceBodyProps) {
  const s = detail.structured ?? {};
  // Every column in the export's order: a list of { name, value } (an object's key order is not kept in storage).
  const fields: (readonly [string, string])[] = Array.isArray(s.fields)
    ? (s.fields as unknown[]).flatMap((f) =>
        f && typeof f === 'object' ? [[textOf((f as Record<string, unknown>).name), textOf((f as Record<string, unknown>).value)] as const] : [],
      )
    : [];
  const id = textOf(s.id);
  const title = textOf(s.title);
  const priority = textOf(s.priority);
  const state = textOf(s.state);
  const opened = textOf(s.openedAt);
  const p = priority ? priorityOf(priority) : null;

  return (
    <div className="space-y-3" data-testid="ticket-view">
      <div className="flex flex-wrap items-center gap-2">
        {id && <span className="font-mono text-body font-medium text-ink-primary" data-testid="ticket-id">{id}</span>}
        {p && <Badge tone={p.tone}>{p.label}</Badge>}
        {state && <Badge tone="neutral">{state}</Badge>}
        {opened && <span className="text-caption text-ink-tertiary">Opened {opened}</span>}
      </div>
      {title && <p className="text-body font-medium text-ink-primary">{title}</p>}

      {fields.length > 0 ? (
        <dl className="divide-y divide-line-subtle rounded-md border border-line-subtle" data-testid="ticket-fields">
          {fields.map(([name, value]) => {
            const long = value.length > 80 || value.includes('\n');
            return (
              <div key={name} className={long ? 'px-3 py-2' : 'grid gap-1 px-3 py-2 sm:grid-cols-[9rem_1fr]'}>
                <dt className="font-mono text-micro text-ink-tertiary">{name}</dt>
                <dd className="mt-0.5 whitespace-pre-wrap break-words text-body text-ink-primary sm:mt-0">{value}</dd>
              </div>
            );
          })}
        </dl>
      ) : (
        <pre className="whitespace-pre-wrap break-words rounded-md border border-line-subtle bg-sunken p-3 font-mono text-caption text-ink-primary" data-testid="evidence-text">
          {detail.text}
        </pre>
      )}

      <SourceLines detail={detail} />
    </div>
  );
}
