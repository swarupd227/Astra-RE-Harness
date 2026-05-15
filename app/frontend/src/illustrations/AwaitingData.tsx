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
      <circle cx="80" cy="80" r="48" fill="#FFFFFF" stroke="#101728" strokeWidth="1.5" />
      <circle cx="80" cy="80" r="48" stroke="#FBE7D6" strokeWidth="6" strokeDasharray="6 4" />
      {/* Center */}
      <circle cx="80" cy="80" r="3" fill="#101728" />
      {/* Hands */}
      <path d="M80 80 L80 50" stroke="#101728" strokeWidth="2.4" strokeLinecap="round" />
      <path d="M80 80 L100 90" stroke="#E5732C" strokeWidth="2.4" strokeLinecap="round" />
      {/* Tick marks */}
      <path d="M80 36 L80 40" stroke="#101728" strokeWidth="1.5" strokeLinecap="round" />
      <path d="M80 120 L80 124" stroke="#101728" strokeWidth="1.5" strokeLinecap="round" />
      <path d="M36 80 L40 80" stroke="#101728" strokeWidth="1.5" strokeLinecap="round" />
      <path d="M120 80 L124 80" stroke="#101728" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  );
}
