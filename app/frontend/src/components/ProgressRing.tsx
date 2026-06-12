/**
 * Marvell-style SVG progress ring. Inspired by the CUDE compliance ring:
 * stroke-dasharray animates as `score` changes, label colour-codes by
 * threshold, big tabular-nums percentage centred inside.
 */
export function ProgressRing({
  score,
  thresholds = { good: 75, warn: 50 },
  label = 'COMPLETE',
  size = 132,
}: {
  /** 0–100 */
  score: number;
  thresholds?: { good: number; warn: number };
  label?: string;
  size?: number;
}) {
  const r = (size - 28) / 2;
  const circ = 2 * Math.PI * r;
  const clamped = Math.max(0, Math.min(100, score));
  const dash = circ * (clamped / 100);
  // Light-theme friendly emerald / amber / rose set.
  const colour =
    clamped >= thresholds.good ? '#059669' :
    clamped >= thresholds.warn ? '#d97706' :
    '#e11d48';
  const stateLabel =
    clamped >= thresholds.good ? 'ON TRACK' :
    clamped >= thresholds.warn ? 'NEEDS ATTENTION' :
    'AT RISK';

  return (
    <div className="flex flex-col items-center justify-center gap-1">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        {/* Track — light slate-200 on white, much subtler than the
            previous dark slate-800 track. */}
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="#e2e8f0" strokeWidth={12} />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          fill="none"
          stroke={colour}
          strokeWidth={12}
          strokeDasharray={`${dash} ${circ}`}
          strokeDashoffset={circ * 0.25}
          strokeLinecap="round"
          style={{ transition: 'stroke-dasharray 800ms ease' }}
        />
        <text
          x={size / 2}
          y={size / 2 - 4}
          textAnchor="middle"
          fill="#0f172a"
          fontSize="24"
          fontWeight={800}
          fontFamily="Inter"
        >
          {Math.round(clamped)}%
        </text>
        <text
          x={size / 2}
          y={size / 2 + 14}
          textAnchor="middle"
          fill="#94a3b8"
          fontSize="8"
          fontFamily="Inter"
          fontWeight={500}
        >
          {label}
        </text>
      </svg>
      <div className="text-[10px] font-bold tracking-wider" style={{ color: colour }}>
        {stateLabel}
      </div>
    </div>
  );
}
