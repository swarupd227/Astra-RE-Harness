import { Link } from 'react-router-dom';
import { CircleHelp } from 'lucide-react';
import { LogoLockup } from '@/components/Logo';
import { Badge } from '@/components/Badge';
import { CommandBarTrigger } from '@/components/CommandBarTrigger';
import { ApiLatencyIndicator } from '@/components/ApiLatencyIndicator';
import { PersonaMenu } from '@/shell/PersonaMenu';
import { Tooltip } from '@/components/Tooltip';

export function TopBar({ onOpenHelp }: { onOpenHelp: () => void }) {
  return (
    <header className="sticky top-0 z-30 flex h-14 items-center gap-4 border-b border-border-subtle bg-raised px-6">
      <Link
        to="/"
        className="inline-flex items-center gap-3 rounded-md transition-opacity duration-fast hover:opacity-80 focus-visible:outline-2 focus-visible:outline-ink-primary"
      >
        <LogoLockup size="md" />
      </Link>
      <Badge tone="neutral" className="font-mono uppercase">DEV</Badge>

      <div className="ml-2 hidden xl:block">
        <CommandBarTrigger />
      </div>

      <div className="ml-auto flex items-center gap-2">
        <ApiLatencyIndicator />
        <Tooltip content="Keyboard shortcuts (?)">
          <button
            type="button"
            onClick={onOpenHelp}
            className="flex h-8 w-8 items-center justify-center rounded-md text-ink-secondary transition-colors duration-fast hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
            aria-label="Open keyboard help"
          >
            <CircleHelp className="h-4 w-4" aria-hidden="true" />
          </button>
        </Tooltip>
        <PersonaMenu />
      </div>
    </header>
  );
}
