export function NoCorporaIllustration({ size = 160 }: { size?: number }) {
  return (
    <svg
      role="img"
      aria-label="No source corpora connected yet"
      width={size}
      height={size}
      viewBox="0 0 160 160"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <title>No source corpora connected yet</title>
      {/* Folder back */}
      <path
        d="M30 50 L70 50 L78 58 L130 58 L130 122 L30 122 Z"
        strokeWidth="1.5"
        strokeLinejoin="round"
        className="fill-sunken stroke-ink-primary"
      />
      {/* Folder fold */}
      <path d="M30 64 L130 64" strokeWidth="1.5" className="stroke-ink-primary" />
      {/* Circuit traces inside */}
      <path d="M50 80 L60 80 L60 92 L75 92" strokeWidth="1.2" strokeLinecap="round" className="stroke-ink-primary" />
      <path d="M85 80 L100 80 L100 100 L115 100" strokeWidth="1.2" strokeLinecap="round" className="stroke-ink-primary" />
      <circle cx="50" cy="80" r="2" className="fill-ink-primary" />
      <circle cx="115" cy="100" r="2" className="fill-ink-primary" />
      <circle cx="75" cy="92" r="2" className="fill-ink-secondary" />
      {/* Plus badge — the one primary action on the page, so it carries the accent */}
      <circle cx="118" cy="48" r="14" strokeWidth="1.5" className="fill-volt stroke-ink-primary" />
      <path d="M118 41 L118 55 M111 48 L125 48" strokeWidth="2" strokeLinecap="round" className="stroke-on-volt" />
    </svg>
  );
}
