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
      <rect x="36" y="36" width="72" height="92" rx="3" fill="#FFFFFF" stroke="#101728" strokeWidth="1.5" />
      {/* Lines */}
      <path d="M48 56 L96 56" stroke="#CFD3DA" strokeWidth="1.5" strokeLinecap="round" />
      <path d="M48 68 L88 68" stroke="#CFD3DA" strokeWidth="1.5" strokeLinecap="round" />
      <path d="M48 80 L80 80" stroke="#CFD3DA" strokeWidth="1.5" strokeLinecap="round" />
      {/* Magnifier */}
      <circle cx="106" cy="100" r="22" fill="#FBE7D6" stroke="#101728" strokeWidth="1.5" />
      <path d="M122 116 L138 132" stroke="#101728" strokeWidth="3" strokeLinecap="round" />
      {/* Empty marker */}
      <path d="M96 100 L116 100" stroke="#101728" strokeWidth="2" strokeLinecap="round" />
      <circle cx="106" cy="100" r="3" fill="#E5732C" />
    </svg>
  );
}
