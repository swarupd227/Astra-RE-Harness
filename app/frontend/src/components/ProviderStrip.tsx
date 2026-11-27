import { Cpu } from 'lucide-react';
import type { ProviderInfo } from '@/lib/extractionStream';

export function ProviderStrip({ info, latencyMs, tokens }: {
  info: ProviderInfo | null;
  latencyMs?: number;
  tokens?: { in: number; out: number };
}) {
  if (!info) {
    return (
      <div className="flex items-center gap-2 px-4 py-1.5 font-mono text-[11px] text-ink-tertiary">
        <Cpu className="h-3 w-3" aria-hidden="true" />
        Negotiating provider…
      </div>
    );
  }
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 border-y border-line-subtle bg-sunken/60 px-4 py-1.5 font-mono text-[11px] text-ink-tertiary">
      <span className="inline-flex items-center gap-1.5">
        <Cpu className="h-3 w-3" aria-hidden="true" />
        <span>{info.name}</span>
        <span className="text-ink-tertiary/60">·</span>
        <span>{info.model}</span>
      </span>
      <span>
        prompt <span className="text-ink-secondary">{info.promptTemplateId}@{info.promptTemplateVersion}</span>
      </span>
      {/* The provider config is an internal string ("anthropic:zdr=true:no-train:…"); say what it means. */}
      <span title={info.configVersion}>
        {/zdr=true/.test(info.configVersion ?? '') || /no-train/.test(info.configVersion ?? '')
          ? <span className="text-ink-secondary">zero data retention · not used for training</span>
          : <>config <span className="text-ink-secondary">{info.configVersion}</span></>}
      </span>
      {tokens && (
        <span>
          tokens <span className="text-ink-secondary">{tokens.in} in / {tokens.out} out</span>
        </span>
      )}
      {latencyMs !== undefined && (
        <span>took <span className="text-ink-secondary">{latencyMs < 1000 ? `${latencyMs} ms` : latencyMs < 60_000 ? `${(latencyMs / 1000).toFixed(1)} s` : `${Math.floor(latencyMs / 60_000)} min ${Math.round((latencyMs % 60_000) / 1000)} s`}</span></span>
      )}
    </div>
  );
}
