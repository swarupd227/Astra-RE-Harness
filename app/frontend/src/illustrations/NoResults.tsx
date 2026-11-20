export function NoResultsIllustration({ size = 160 }: { size?: number }) {
  return (
    <svg
      role="img"
      aria-label="No matching results"
      width={size}
      height={size}
      viewBox="0 0 160 160"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <title>No matching results</title>
      {/* Document */}
      <rect x="36" y="36" width="72" height="92" rx="3" strokeWidth="1.5" className="fill-raised stroke-ink-primary" />
      {/* Lines */}
      <path d="M48 56 L96 56" strokeWidth="1.5" strokeLinecap="round" className="stroke-line" />
      <path d="M48 68 L88 68" strokeWidth="1.5" strokeLinecap="round" className="stroke-line" />
      <path d="M48 80 L80 80" strokeWidth="1.5" strokeLinecap="round" className="stroke-line" />
      {/* Magnifier */}
      <circle cx="106" cy="100" r="22" strokeWidth="1.5" className="fill-sunken stroke-ink-primary" />
      <path d="M122 116 L138 132" strokeWidth="3" strokeLinecap="round" className="stroke-ink-primary" />
      {/* Empty marker */}
      <path d="M96 100 L116 100" strokeWidth="2" strokeLinecap="round" className="stroke-ink-primary" />
      <circle cx="106" cy="100" r="3" className="fill-ink-tertiary" />
    </svg>
  );
}
