#!/usr/bin/env node
/**
 * Trim the Playwright demo video.
 *
 * After the minpack-demo test runs with RECORD_DEMO=1, this script:
 *   1. Locates the most recent test-results/<...>/video.webm + timeline.json
 *   2. Reads the [{ startMs, endMs, label }] llm-wait segments
 *   3. Builds an ffmpeg filter_complex that:
 *        - keeps every [non-wait] segment
 *        - replaces each wait segment with a 1.5 s caption card
 *          ("⏳ Anthropic · <label> · skipped <duration>s")
 *        - concatenates the result into demo.mp4
 *
 * Runs ffmpeg via the linuxserver/ffmpeg docker image so we don't need a
 * host install. The video lives on the host filesystem under test-results
 * and is bind-mounted into the container.
 *
 * Usage:
 *   node scripts/trim-demo.mjs                 # picks newest test-results video
 *   node scripts/trim-demo.mjs path/to/dir     # uses that specific test dir
 */
import { readdirSync, statSync, existsSync, readFileSync, copyFileSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname, basename } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const E2E_ROOT = resolve(__dirname, '..');
const TEST_RESULTS = join(E2E_ROOT, 'test-results');

// Small fudge factor: Playwright starts recording at context creation
// (~150-300 ms before the first user-driven action), so our t0 sits a
// bit later than the video's t0. Trim the wait windows slightly later
// to compensate — feels right after eyeballing the result.
const VIDEO_T0_OFFSET_MS = 250;

// How long the caption card stays on screen between cuts.
const CAPTION_HOLD_S = 1.5;

function readTimeline() {
  // The test writes the timeline to a fixed ASCII path because the per-test
  // outputDir name contains Unicode chars that Node's fs occasionally trips
  // on under Windows. Read from that fixed path and use the outputDir field
  // (recorded by the test) to find the video.
  const fixedPath = join(TEST_RESULTS, 'timeline-latest.json');
  if (!existsSync(fixedPath))
    throw new Error(`No ${fixedPath}. Did you run minpack-demo with RECORD_DEMO=1?`);
  return JSON.parse(readFileSync(fixedPath, 'utf8'));
}

function findVideo(dir) {
  // Playwright drops video.webm at the top of the test's output dir.
  const direct = join(dir, 'video.webm');
  if (existsSync(direct)) return direct;
  // Some flows nest by browser context; fall back to a recursive search.
  for (const name of readdirSync(dir)) {
    const sub = join(dir, name);
    if (statSync(sub).isDirectory()) {
      const inner = join(sub, 'video.webm');
      if (existsSync(inner)) return inner;
    }
  }
  throw new Error(`No video.webm under ${dir}`);
}

function fmtSec(s) {
  return s.toFixed(3);
}

function dockerFFmpeg(workdirHost, args) {
  // Bind-mount the dir holding video.webm at /work inside the container.
  // linuxserver/ffmpeg accepts ffmpeg flags as the command.
  const dockerArgs = [
    'run', '--rm',
    '-v', `${workdirHost}:/work`,
    '-w', '/work',
    '--entrypoint', '/usr/local/bin/ffmpeg',
    'linuxserver/ffmpeg:latest',
    ...args,
  ];
  const res = spawnSync('docker', dockerArgs, { stdio: 'inherit' });
  if (res.status !== 0) {
    throw new Error(`ffmpeg exited with ${res.status} (signal ${res.signal})`);
  }
}

function escapeDrawText(s) {
  // ffmpeg drawtext: backslash-escape colons, single quotes, commas,
  // and the backslash itself. Keep the string ASCII-safe for libfreetype.
  return s
    .replace(/\\/g, '\\\\')
    .replace(/'/g, "\\'")
    .replace(/:/g, '\\:')
    .replace(/,/g, '\\,');
}

const timeline = readTimeline();
// Use the outputDir recorded in the timeline to find the video. If the
// script user passed an explicit dir on argv we honour that instead.
const sourceDir = process.argv[2] ? resolve(process.argv[2]) : timeline.outputDir;
if (!sourceDir) throw new Error('timeline-latest.json is missing the outputDir field.');
console.log(`[trim-demo] source dir: ${sourceDir}`);

const sourceVideo = findVideo(sourceDir);

// Docker Desktop on Windows is shaky about Unicode in bind-mount paths
// ("·", "→" in Playwright's auto-encoded dir names). Copy the video to
// an ASCII-only working dir before running ffmpeg, then write the final
// demo.mp4 there.
const videoDir = join(TEST_RESULTS, 'trim-work');
mkdirSync(videoDir, { recursive: true });
const videoName = 'video.webm';
copyFileSync(sourceVideo, join(videoDir, videoName));
console.log(`[trim-demo] staged video at ${join(videoDir, videoName)}`);

// videoTotalMs is the test's t0-relative duration. The actual video runs
// a bit longer because recording continues past the last action until the
// browser context closes. We use ffprobe via docker to get the real video
// length, falling back to videoTotalMs if probe fails.
let videoDurationS;
{
  const probeArgs = [
    'run', '--rm',
    '-v', `${videoDir}:/work`,
    '-w', '/work',
    '--entrypoint', '/usr/local/bin/ffprobe',
    'linuxserver/ffmpeg:latest',
    '-v', 'error', '-show_entries', 'format=duration',
    '-of', 'default=noprint_wrappers=1:nokey=1',
    videoName,
  ];
  const probe = spawnSync('docker', probeArgs, { encoding: 'utf8' });
  if (probe.status === 0) {
    videoDurationS = parseFloat(probe.stdout.trim());
  } else {
    videoDurationS = timeline.videoTotalMs / 1000;
  }
}
console.log(`[trim-demo] video duration: ${fmtSec(videoDurationS)} s`);

// Build the cut list. We keep [0, wait1.startS], replace [wait1.startS, wait1.endS]
// with a caption, then keep [wait1.endS, wait2.startS], and so on.
const waits = timeline.entries
  .map((e) => ({
    label: e.label,
    startS: Math.max(0, (e.startMs + VIDEO_T0_OFFSET_MS) / 1000),
    endS: Math.max(0, (e.endMs + VIDEO_T0_OFFSET_MS) / 1000),
  }))
  .sort((a, b) => a.startS - b.startS);

if (waits.length === 0) {
  console.log('[trim-demo] no llm-wait entries; copying as-is to demo.mp4');
  dockerFFmpeg(videoDir, ['-y', '-i', videoName, '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
                          '-preset', 'medium', '-crf', '20', 'demo.mp4']);
  process.exit(0);
}

console.log(`[trim-demo] cutting ${waits.length} llm-wait segment(s):`);
for (const w of waits) {
  console.log(`           [${fmtSec(w.startS)}s → ${fmtSec(w.endS)}s] (${fmtSec(w.endS - w.startS)}s) ${w.label}`);
}

// We render each kept segment + each caption to its own intermediate file,
// then concat them with the demuxer. This is simpler than one giant
// filter_complex and lets ffmpeg cache the heavy re-encode work per part.
const parts = [];
let cursorS = 0;
let idx = 0;

function nextPart() {
  const name = `part_${String(idx).padStart(3, '0')}.mp4`;
  idx += 1;
  return name;
}

// Probe the source video dimensions for caption rendering.
let width = 1600, height = 1000;
{
  const probeArgs = [
    'run', '--rm',
    '-v', `${videoDir}:/work`,
    '-w', '/work',
    '--entrypoint', '/usr/local/bin/ffprobe',
    'linuxserver/ffmpeg:latest',
    '-v', 'error', '-select_streams', 'v:0',
    '-show_entries', 'stream=width,height',
    '-of', 'csv=p=0',
    videoName,
  ];
  const probe = spawnSync('docker', probeArgs, { encoding: 'utf8' });
  if (probe.status === 0) {
    const m = probe.stdout.trim().match(/^(\d+),(\d+)$/);
    if (m) { width = parseInt(m[1], 10); height = parseInt(m[2], 10); }
  }
}
console.log(`[trim-demo] source dimensions: ${width}x${height}`);

for (const w of waits) {
  // Keep [cursor, w.startS] if non-empty.
  if (w.startS > cursorS + 0.05) {
    const segName = nextPart();
    parts.push(segName);
    dockerFFmpeg(videoDir, [
      '-y',
      '-ss', fmtSec(cursorS),
      '-to', fmtSec(w.startS),
      '-i', videoName,
      '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
      '-preset', 'medium', '-crf', '20',
      '-an',
      segName,
    ]);
  }
  // Caption card for the cut segment. Font sizes scale to width so the
  // text fits even when Playwright records at half the viewport (800×500
  // on a 1600×1000 viewport).
  const titleFontSize = Math.round(width * 0.038);   // ~30 px at 800w, 61 at 1600w
  const subtitleFontSize = Math.round(width * 0.022); // ~18 px at 800w, 35 at 1600w
  const yOffset = Math.round(height * 0.06);
  const skippedS = Math.max(0.1, w.endS - w.startS);
  const captionLine1 = `Anthropic real call (skipped)`;
  const captionLine2 = `${w.label}  ·  ${skippedS.toFixed(0)}s elapsed`;
  const segName = nextPart();
  parts.push(segName);
  dockerFFmpeg(videoDir, [
    '-y',
    '-f', 'lavfi',
    '-i', `color=c=0x111827:s=${width}x${height}:d=${CAPTION_HOLD_S}`,
    '-vf',
    `drawtext=fontfile=/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf:text='${escapeDrawText(captionLine1)}':fontcolor=0xF9FAFB:fontsize=${titleFontSize}:x=(w-text_w)/2:y=(h-text_h)/2-${yOffset},` +
    `drawtext=fontfile=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf:text='${escapeDrawText(captionLine2)}':fontcolor=0x9CA3AF:fontsize=${subtitleFontSize}:x=(w-text_w)/2:y=(h-text_h)/2+${yOffset}`,
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
    '-preset', 'medium', '-crf', '20',
    '-an',
    segName,
  ]);
  cursorS = w.endS;
}

// Tail after the last wait.
if (videoDurationS > cursorS + 0.05) {
  const segName = nextPart();
  parts.push(segName);
  dockerFFmpeg(videoDir, [
    '-y',
    '-ss', fmtSec(cursorS),
    '-i', videoName,
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
    '-preset', 'medium', '-crf', '20',
    '-an',
    segName,
  ]);
}

// Concat demuxer needs a listing file.
const listPath = join(videoDir, 'concat.txt');
const fs = await import('node:fs/promises');
await fs.writeFile(listPath, parts.map((p) => `file '${p}'`).join('\n') + '\n', 'utf8');

dockerFFmpeg(videoDir, [
  '-y',
  '-f', 'concat', '-safe', '0', '-i', 'concat.txt',
  '-c', 'copy',
  'demo.mp4',
]);

// Clean up intermediates so the directory isn't cluttered with part_*.mp4.
for (const p of parts) {
  try { await fs.unlink(join(videoDir, p)); } catch { /* ignore */ }
}
try { await fs.unlink(listPath); } catch { /* ignore */ }

const outPath = join(videoDir, 'demo.mp4');
const finalStat = statSync(outPath);
console.log(`\n[trim-demo] wrote ${outPath} (${(finalStat.size / 1024 / 1024).toFixed(1)} MiB)`);
console.log(`[trim-demo] cut ${waits.reduce((s, w) => s + (w.endS - w.startS), 0).toFixed(1)}s of LLM-wait;`
  + ` added ${(waits.length * CAPTION_HOLD_S).toFixed(1)}s of caption.`);
