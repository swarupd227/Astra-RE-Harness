export function AwaitingDataIllustration({ size = 160 }: { size?: number }) {
  return (
    <svg
      role="img"
      aria-label="Awaiting data"
      width={size}
      height={size}
      viewBox="0 0 160 160"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <title>Awaiting data</title>
      {/* Clock body */}
      <circle cx="80" cy="80" r="48" strokeWidth="1.5" className="fill-raised stroke-ink-primary" />
      <circle cx="80" cy="80" r="48" strokeWidth="6" strokeDasharray="6 4" className="stroke-line" />
      {/* Center */}
      <circle cx="80" cy="80" r="3" className="fill-ink-primary" />
      {/* Hands */}
      <path d="M80 80 L80 50" strokeWidth="2.4" strokeLinecap="round" className="stroke-ink-primary" />
      <path d="M80 80 L100 90" strokeWidth="2.4" strokeLinecap="round" className="stroke-status-warn" />
      {/* Tick marks */}
      <path d="M80 36 L80 40" strokeWidth="1.5" strokeLinecap="round" className="stroke-ink-primary" />
      <path d="M80 120 L80 124" strokeWidth="1.5" strokeLinecap="round" className="stroke-ink-primary" />
      <path d="M36 80 L40 80" strokeWidth="1.5" strokeLinecap="round" className="stroke-ink-primary" />
      <path d="M120 80 L124 80" strokeWidth="1.5" strokeLinecap="round" className="stroke-ink-primary" />
    </svg>
  );
}
