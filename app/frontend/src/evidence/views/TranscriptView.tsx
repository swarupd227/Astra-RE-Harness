import { useCallback, useRef, useState } from 'react';
import { clsx } from 'clsx';
import { Play } from 'lucide-react';
import { artefactContentUrl } from '@/lib/api';
import { Button } from '@/components/Button';
import { SourceLines, textOf, type EvidenceBodyProps } from '@/evidence/views/DefaultBody';

/** 872 → "14:32", 3723 → "1:02:03". */
export function clock(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}

type Facts = { speaker: string; start: number | null; end: number | null };

function factsOf(s: Record<string, unknown> | null | undefined): Facts {
  const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
  return { speaker: textOf(s?.speaker), start: num(s?.startSeconds), end: num(s?.endSeconds) };
}

/** The words of a turn without the "Speaker: " the chunk text starts with. */
function wordsOf(text: string | null | undefined, speaker: string): string {
  const t = text ?? '';
  return speaker && t.startsWith(`${speaker}: `) ? t.slice(speaker.length + 2) : t;
}

/**
 * A moment in a transcript: the turn that was cited, highlighted, with the turns either side of it, and
 * — when a recording is linked — a player that starts at the cited moment and stops where it ends. The
 * times beside the neighbouring turns play from there. Without a recording it says so, rather than
 * showing a player that cannot play.
 */
export function TranscriptView({ detail }: EvidenceBodyProps) {
  const audio = detail.audio ?? null;
  const here = factsOf(detail.structured);
  const before = detail.context?.before ?? [];
  const after = detail.context?.after ?? [];

  const player = useRef<HTMLAudioElement>(null);
  const stopAt = useRef<number | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  const playFrom = useCallback((from: number, until: number | null) => {
    const el = player.current;
    if (!el) return;
    setProblem(null);
    stopAt.current = until;
    const go = () => {
      el.currentTime = from;
      void el.play().catch((e: unknown) => {
        setProblem(e instanceof Error && e.name === 'NotAllowedError'
          ? 'The browser did not allow playback. Press play on the recording.'
          : 'The recording could not be played.');
      });
    };
    // A seek before the recording's length is known is ignored, so wait for it.
    if (el.readyState >= 1) go();
    else el.addEventListener('loadedmetadata', go, { once: true });
  }, []);

  return (
    <div className="space-y-3" data-testid="transcript-view">
      {audio ? (
        <div className="space-y-2 rounded-md border border-line-subtle bg-sunken p-3" data-testid="transcript-player">
          <div className="flex flex-wrap items-center gap-2">
            {here.start !== null && (
              <Button
                variant="secondary"
                size="sm"
                onClick={() => playFrom(here.start!, here.end)}
                data-testid="transcript-play-moment"
              >
                <Play className="h-3.5 w-3.5" aria-hidden="true" />
                Play this moment · {clock(here.start)}
              </Button>
            )}
            <span className="min-w-0 truncate font-mono text-micro text-ink-tertiary" title={audio.path}>
              {audio.path}
            </span>
          </div>
          <audio
            ref={player}
            controls
            preload="metadata"
            src={artefactContentUrl(audio.fileId)}
            className="w-full"
            data-testid="transcript-audio"
            onTimeUpdate={(e) => {
              if (stopAt.current !== null && e.currentTarget.currentTime >= stopAt.current) {
                e.currentTarget.pause();
                stopAt.current = null;
              }
            }}
            onEnded={() => {
              stopAt.current = null;
            }}
            onError={() => setProblem('The recording could not be loaded.')}
          />
          {problem && (
            <p className="text-caption text-status-fail" role="alert" data-testid="transcript-audio-problem">
              {problem}
            </p>
          )}
        </div>
      ) : (
        <p className="text-caption text-ink-tertiary" data-testid="transcript-no-audio">
          No recording is linked to this transcript, so there is nothing to play.
        </p>
      )}

      <ol className="space-y-2" data-testid="transcript-turns">
        {before.map((n) => {
          const f = factsOf(n.structured);
          return (
            <Turn
              key={n.shortId}
              speaker={f.speaker}
              start={f.start}
              words={wordsOf(n.text, f.speaker)}
              onPlay={audio && f.start !== null ? () => playFrom(f.start!, f.end) : undefined}
            />
          );
        })}
        <Turn
          current
          speaker={here.speaker}
          start={here.start}
          words={wordsOf(detail.text, here.speaker)}
          onPlay={audio && here.start !== null ? () => playFrom(here.start!, here.end) : undefined}
        />
        {after.map((n) => {
          const f = factsOf(n.structured);
          return (
            <Turn
              key={n.shortId}
              speaker={f.speaker}
              start={f.start}
              words={wordsOf(n.text, f.speaker)}
              onPlay={audio && f.start !== null ? () => playFrom(f.start!, f.end) : undefined}
            />
          );
        })}
      </ol>

      <SourceLines detail={detail} />
    </div>
  );
}

function Turn({
  speaker,
  start,
  words,
  current,
  onPlay,
}: {
  speaker: string;
  start: number | null;
  words: string;
  current?: boolean;
  onPlay?: () => void;
}) {
  const time = start === null ? null : clock(start);
  return (
    <li
      className={clsx('rounded-md px-3 py-2', current ? 'bg-volt/10' : 'bg-sunken')}
      data-testid={current ? 'transcript-turn-current' : 'transcript-turn'}
      aria-current={current ? 'true' : undefined}
    >
      <div className="flex items-baseline gap-2 text-micro">
        {speaker && <span className="font-medium text-ink-primary">{speaker}</span>}
        {time &&
          (onPlay ? (
            <button
              type="button"
              onClick={onPlay}
              aria-label={`Play from ${time}`}
              className="font-mono tabular-nums text-ink-tertiary underline-offset-2 hover:text-ink-primary hover:underline focus-visible:outline-2 focus-visible:outline-volt"
              data-testid="transcript-turn-time"
            >
              {time}
            </button>
          ) : (
            <span className="font-mono tabular-nums text-ink-tertiary">{time}</span>
          ))}
      </div>
      <p className={clsx('mt-1 whitespace-pre-wrap break-words text-body', current ? 'text-ink-primary' : 'text-ink-secondary')}>
        {words}
      </p>
    </li>
  );
}
