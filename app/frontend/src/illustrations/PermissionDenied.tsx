export function PermissionDeniedIllustration({ size = 160 }: { size?: number }) {
  return (
    <svg
      role="img"
      aria-label="Permission denied"
      width={size}
      height={size}
      viewBox="0 0 160 160"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <title>Permission denied</title>
      {/* Shield outline */}
      <path
        d="M80 24 L120 38 L120 78 C120 102 100 122 80 132 C60 122 40 102 40 78 L40 38 Z"
        fill="#FBE7D6"
        stroke="#101728"
        strokeWidth="1.5"
        strokeLinejoin="round"
      />
      {/* X mark */}
      <path d="M62 62 L98 98" stroke="#A8201A" strokeWidth="3" strokeLinecap="round" />
      <path d="M98 62 L62 98" stroke="#A8201A" strokeWidth="3" strokeLinecap="round" />
      {/* Bezel notch */}
      <circle cx="80" cy="38" r="2" fill="#E5732C" />
    </svg>
  );
}
