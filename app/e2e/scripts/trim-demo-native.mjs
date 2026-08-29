#!/usr/bin/env node
/**
 * Native-ffmpeg variant of trim-demo.mjs.
 *
 * trim-demo.mjs shells out to `docker run linuxserver/ffmpeg` for every
 * ffmpeg call, which needs Docker Desktop running. This machine has a full
 * native ffmpeg/ffprobe build on PATH already, so this variant runs the
 * identical filter logic directly — same segment-cut-and-caption approach,
 * same output codec/resolution conventions (1600x1000, 25fps, libx264,
 * yuv420p, crf 20) so it still concats losslessly with the brand cards.
 *
 * Font: Windows Arial (bold/regular) instead of DejaVu — cosmetic only.
 *
 * Usage:
 *   node scripts/trim-demo-native.mjs                 # picks newest test-results video
 *   node scripts/trim-demo-native.mjs path/to/dir     # uses that specific test dir
 */
import { readdirSync, statSync, existsSync, readFileSync, copyFileSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const E2E_ROOT = resolve(__dirname, '..');
const TEST_RESULTS = join(E2E_ROOT, 'test-results');

const VIDEO_T0_OFFSET_MS = 250;
const CAPTION_HOLD_S = 1.5;
// Referenced as bare filenames (copied into the working dir below) because
// ffmpeg's drawtext filter parses ':' as an option separator — a Windows
// drive-letter path like "C:/Windows/Fonts/..." breaks mid-parse even
// inside quotes.
const FONT_BOLD = 'arialbd.ttf';
const FONT_REG = 'arial.ttf';

function readTimeline() {
  const fixedPath = join(TEST_RESULTS, 'timeline-latest.json');
  if (!existsSync(fixedPath))
    throw new Error(`No ${fixedPath}. Did you run unibasic-demo with RECORD_DEMO=1?`);
  return JSON.parse(readFileSync(fixedPath, 'utf8'));
}

function findVideo(dir) {
  const direct = join(dir, 'video.webm');
  if (existsSync(direct)) return direct;
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

function ffmpeg(workdirHost, args) {
  const res = spawnSync('ffmpeg', args, { cwd: workdirHost, stdio: 'inherit' });
  if (res.status !== 0) {
    throw new Error(`ffmpeg exited with ${res.status} (signal ${res.signal})`);
  }
}

function ffprobe(workdirHost, args) {
  return spawnSync('ffprobe', args, { cwd: workdirHost, encoding: 'utf8' });
}

function escapeDrawText(s) {
  return s
    .replace(/\\/g, '\\\\')
    .replace(/'/g, "\\'")
    .replace(/:/g, '\\:')
    .replace(/,/g, '\\,');
}

const timeline = readTimeline();
const sourceDir = process.argv[2] ? resolve(process.argv[2]) : timeline.outputDir;
if (!sourceDir) throw new Error('timeline-latest.json is missing the outputDir field.');
console.log(`[trim-demo-native] source dir: ${sourceDir}`);

const sourceVideo = findVideo(sourceDir);

const videoDir = join(TEST_RESULTS, 'trim-work');
mkdirSync(videoDir, { recursive: true });
const videoName = 'video.webm';
copyFileSync(sourceVideo, join(videoDir, videoName));
console.log(`[trim-demo-native] staged video at ${join(videoDir, videoName)}`);
copyFileSync('C:/Windows/Fonts/arialbd.ttf', join(videoDir, FONT_BOLD));
copyFileSync('C:/Windows/Fonts/arial.ttf', join(videoDir, FONT_REG));

let videoDurationS;
{
  const probe = ffprobe(videoDir, [
    '-v', 'error', '-show_entries', 'format=duration',
    '-of', 'default=noprint_wrappers=1:nokey=1',
    videoName,
  ]);
  if (probe.status === 0) {
    videoDurationS = parseFloat(probe.stdout.trim());
  } else {
    videoDurationS = timeline.videoTotalMs / 1000;
  }
}
console.log(`[trim-demo-native] video duration: ${fmtSec(videoDurationS)} s`);

const waits = timeline.entries
  .map((e) => ({
    label: e.label,
    startS: Math.max(0, (e.startMs + VIDEO_T0_OFFSET_MS) / 1000),
    endS: Math.max(0, (e.endMs + VIDEO_T0_OFFSET_MS) / 1000),
  }))
  .sort((a, b) => a.startS - b.startS);

if (waits.length === 0) {
  console.log('[trim-demo-native] no llm-wait entries; copying as-is to demo.mp4');
  ffmpeg(videoDir, ['-y', '-i', videoName, '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
                     '-preset', 'medium', '-crf', '20', 'demo.mp4']);
  process.exit(0);
}

console.log(`[trim-demo-native] cutting ${waits.length} llm-wait segment(s):`);
for (const w of waits) {
  console.log(`           [${fmtSec(w.startS)}s → ${fmtSec(w.endS)}s] (${fmtSec(w.endS - w.startS)}s) ${w.label}`);
}

const parts = [];
let cursorS = 0;
let idx = 0;

function nextPart() {
  const name = `part_${String(idx).padStart(3, '0')}.mp4`;
  idx += 1;
  return name;
}

let width = 1600, height = 1000;
{
  const probe = ffprobe(videoDir, [
    '-v', 'error', '-select_streams', 'v:0',
    '-show_entries', 'stream=width,height',
    '-of', 'csv=p=0',
    videoName,
  ]);
  if (probe.status === 0) {
    const m = probe.stdout.trim().match(/^(\d+),(\d+)$/);
    if (m) { width = parseInt(m[1], 10); height = parseInt(m[2], 10); }
  }
}
console.log(`[trim-demo-native] source dimensions: ${width}x${height}`);

for (const w of waits) {
  if (w.startS > cursorS + 0.05) {
    const segName = nextPart();
    parts.push(segName);
    ffmpeg(videoDir, [
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
  const titleFontSize = Math.round(width * 0.038);
  const subtitleFontSize = Math.round(width * 0.022);
  const yOffset = Math.round(height * 0.06);
  const captionLine1 = `Regenerating & running the test pack…`;
  const captionLine2 = `Validating against the signed spec`;
  const segName = nextPart();
  parts.push(segName);
  ffmpeg(videoDir, [
    '-y',
    '-f', 'lavfi',
    '-i', `color=c=0x111827:s=${width}x${height}:d=${CAPTION_HOLD_S}`,
    '-vf',
    `drawtext=fontfile='${FONT_BOLD}':text='${escapeDrawText(captionLine1)}':fontcolor=0xF9FAFB:fontsize=${titleFontSize}:x=(w-text_w)/2:y=(h-text_h)/2-${yOffset},` +
    `drawtext=fontfile='${FONT_REG}':text='${escapeDrawText(captionLine2)}':fontcolor=0x9CA3AF:fontsize=${subtitleFontSize}:x=(w-text_w)/2:y=(h-text_h)/2+${yOffset}`,
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
    '-preset', 'medium', '-crf', '20',
    '-an',
    segName,
  ]);
  cursorS = w.endS;
}

if (videoDurationS > cursorS + 0.05) {
  const segName = nextPart();
  parts.push(segName);
  ffmpeg(videoDir, [
    '-y',
    '-ss', fmtSec(cursorS),
    '-i', videoName,
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p',
    '-preset', 'medium', '-crf', '20',
    '-an',
    segName,
  ]);
}

const listPath = join(videoDir, 'concat.txt');
const fs = await import('node:fs/promises');
await fs.writeFile(listPath, parts.map((p) => `file '${p}'`).join('\n') + '\n', 'utf8');

ffmpeg(videoDir, [
  '-y',
  '-f', 'concat', '-safe', '0', '-i', 'concat.txt',
  '-c', 'copy',
  'demo.mp4',
]);

for (const p of parts) {
  try { await fs.unlink(join(videoDir, p)); } catch { /* ignore */ }
}
try { await fs.unlink(listPath); } catch { /* ignore */ }

const outPath = join(videoDir, 'demo.mp4');
const finalStat = statSync(outPath);
console.log(`\n[trim-demo-native] wrote ${outPath} (${(finalStat.size / 1024 / 1024).toFixed(1)} MiB)`);
console.log(`[trim-demo-native] cut ${waits.reduce((s, w) => s + (w.endS - w.startS), 0).toFixed(1)}s of LLM-wait;`
  + ` added ${(waits.length * CAPTION_HOLD_S).toFixed(1)}s of caption.`);
