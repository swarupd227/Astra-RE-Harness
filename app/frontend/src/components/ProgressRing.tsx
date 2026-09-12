import { themeHex, useTheme } from '@/theme';

/**
 * SVG progress ring. stroke-dasharray animates as `score` changes, the
 * colour codes by threshold, big tabular-nums percentage centred inside.
 * Every colour comes from the theme table so the ring follows the toggle.
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
  const { theme } = useTheme();
  const r = (size - 28) / 2;
  const circ = 2 * Math.PI * r;
  const clamped = Math.max(0, Math.min(100, score));
  const dash = circ * (clamped / 100);
  const colour =
    clamped >= thresholds.good ? themeHex('status-ok', theme) :
    clamped >= thresholds.warn ? themeHex('status-warn', theme) :
    themeHex('status-fail', theme);
  const stateLabel =
    clamped >= thresholds.good ? 'ON TRACK' :
    clamped >= thresholds.warn ? 'NEEDS ATTENTION' :
    'AT RISK';

  return (
    <div className="flex flex-col items-center justify-center gap-1">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={`${Math.round(clamped)}% ${label.toLowerCase()}`}>
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={themeHex('line-subtle', theme)} strokeWidth={12} />
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
          fill={themeHex('ink-primary', theme)}
          fontSize="24"
          fontWeight={800}
          fontFamily="Inter Variable, Inter, sans-serif"
        >
          {Math.round(clamped)}%
        </text>
        <text
          x={size / 2}
          y={size / 2 + 14}
          textAnchor="middle"
          fill={themeHex('ink-tertiary', theme)}
          fontSize="8"
          fontFamily="Inter Variable, Inter, sans-serif"
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
