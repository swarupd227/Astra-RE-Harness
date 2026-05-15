#!/usr/bin/env node
/**
 * Generate Piper TTS narration aligned to the trimmed demo video, then
 * mux the assembled audio track onto demo.mp4.
 *
 * Input:
 *   demo-output/narration-timeline.json   — { sections: [{ id, startMs, text }] }
 *   test-results/trim-work/demo.mp4       — trimmed video (from trim-demo.mjs)
 *   demo-output/piper-voices/*.onnx       — voice model
 *
 * Output:
 *   demo-output/demo-narrated.mp4         — final MP4 with narration
 *
 * Pipeline per section:
 *   text → Piper → section-NN.wav
 *
 * Then ffmpeg builds a single audio track:
 *   for each section: pad with silence to (section.startMs - previousEnd)
 *                     append the section's wav
 *                     previousEnd = section.startMs + duration(wav)
 *   if the resulting audio is shorter than the video, pad to match.
 *
 * Then ffmpeg muxes the assembled audio onto demo.mp4.
 *
 * Runs Piper via docker (lscr.io/linuxserver/piper) and ffmpeg via docker
 * (linuxserver/ffmpeg) so neither binary needs to be on the host PATH.
 */
import { existsSync, readFileSync, writeFileSync, statSync, mkdirSync, unlinkSync, readdirSync, copyFileSync } from 'node:fs';
import { join, resolve, dirname, basename } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const E2E_ROOT = resolve(__dirname, '..');
const DEMO_OUT = join(E2E_ROOT, 'demo-output');
const TIMELINE = join(DEMO_OUT, 'narration-timeline.json');
const VOICE_DIR = join(DEMO_OUT, 'piper-voices');
const INPUT_VIDEO = join(E2E_ROOT, 'test-results', 'trim-work', 'demo.mp4');
const WORK_DIR = join(DEMO_OUT, 'narration-work');
const OUTPUT = join(DEMO_OUT, 'demo-narrated.mp4');

if (!existsSync(TIMELINE)) throw new Error(`Missing ${TIMELINE}`);
if (!existsSync(INPUT_VIDEO)) throw new Error(`Missing ${INPUT_VIDEO} — run trim-demo.mjs first.`);
if (!existsSync(VOICE_DIR)) throw new Error(`Missing ${VOICE_DIR} — download a Piper voice model first.`);

const timeline = JSON.parse(readFileSync(TIMELINE, 'utf8'));
const voiceModel = timeline.voiceModel ?? 'en_US-libritts_r-medium';
const voiceOnnx = join(VOICE_DIR, `${voiceModel}.onnx`);
if (!existsSync(voiceOnnx)) throw new Error(`Voice model not at ${voiceOnnx}`);

mkdirSync(WORK_DIR, { recursive: true });
console.log(`[narration] working dir: ${WORK_DIR}`);
console.log(`[narration] voice:       ${voiceModel}`);
console.log(`[narration] video:       ${INPUT_VIDEO}`);
console.log(`[narration] sections:    ${timeline.sections.length}`);

// ──────────────────────────────────────────────────────────────────────
// 1. Generate one wav per section via Piper (in docker)
// ──────────────────────────────────────────────────────────────────────

for (const section of timeline.sections) {
  const wavPath = join(WORK_DIR, `${section.id}.wav`);
  const text = section.text;
  console.log(`[piper] ${section.id}: ${text.slice(0, 60)}${text.length > 60 ? '…' : ''}`);

  const args = [
    'run', '--rm', '-i',
    '-v', `${VOICE_DIR}:/voices`,
    '-v', `${WORK_DIR}:/work`,
    '--entrypoint', '/lsiopy/bin/piper',
    'lscr.io/linuxserver/piper:latest',
    '-m', `/voices/${voiceModel}.onnx`,
    '--output-file', `/work/${section.id}.wav`,
  ];
  const res = spawnSync('docker', args, {
    input: text,
    encoding: 'utf8',
  });
  if (res.status !== 0) {
    console.error(res.stderr);
    throw new Error(`Piper failed for section ${section.id}`);
  }
  if (!existsSync(wavPath))
    throw new Error(`Expected ${wavPath} after Piper run for ${section.id}`);
  const { size } = statSync(wavPath);
  console.log(`         → ${section.id}.wav (${(size / 1024).toFixed(1)} KiB)`);
}

// ──────────────────────────────────────────────────────────────────────
// 2. Probe each wav duration so we know where the next section's silence
//    pad starts.
// ──────────────────────────────────────────────────────────────────────

function probeDurationMs(wavPath) {
  const probeArgs = [
    'run', '--rm',
    '-v', `${WORK_DIR}:/work`,
    '--entrypoint', '/usr/local/bin/ffprobe',
    'linuxserver/ffmpeg:latest',
    '-v', 'error',
    '-show_entries', 'format=duration',
    '-of', 'default=noprint_wrappers=1:nokey=1',
    `/work/${basename(wavPath)}`,
  ];
  const r = spawnSync('docker', probeArgs, { encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`ffprobe failed for ${wavPath}: ${r.stderr}`);
  return Math.round(parseFloat(r.stdout.trim()) * 1000);
}

// Also probe the video duration so the assembled audio matches.
function probeVideoDurationMs(videoPath) {
  const dir = dirname(videoPath);
  const name = basename(videoPath);
  const probeArgs = [
    'run', '--rm',
    '-v', `${dir}:/work`,
    '--entrypoint', '/usr/local/bin/ffprobe',
    'linuxserver/ffmpeg:latest',
    '-v', 'error',
    '-show_entries', 'format=duration',
    '-of', 'default=noprint_wrappers=1:nokey=1',
    `/work/${name}`,
  ];
  const r = spawnSync('docker', probeArgs, { encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`ffprobe video failed: ${r.stderr}`);
  return Math.round(parseFloat(r.stdout.trim()) * 1000);
}

const videoMs = probeVideoDurationMs(INPUT_VIDEO);
console.log(`[narration] video duration: ${(videoMs / 1000).toFixed(1)}s`);

// ──────────────────────────────────────────────────────────────────────
// 3. Build a single audio track with silence pads between sections.
//    Use ffmpeg's concat demuxer: a list file pointing at section wavs
//    and synthesised silence wavs (one per gap).
// ──────────────────────────────────────────────────────────────────────

const sections = [...timeline.sections].sort((a, b) => a.startMs - b.startMs);
let cursorMs = 0;
const concatItems = [];   // { kind: 'silence'|'section', path, durationMs }
let silenceIdx = 0;

function genSilence(durationMs) {
  if (durationMs <= 0) return null;
  const name = `silence_${String(silenceIdx).padStart(3, '0')}.wav`;
  silenceIdx += 1;
  const args = [
    'run', '--rm',
    '-v', `${WORK_DIR}:/work`,
    '--entrypoint', '/usr/local/bin/ffmpeg',
    'linuxserver/ffmpeg:latest',
    '-y',
    '-f', 'lavfi',
    '-i', `anullsrc=channel_layout=mono:sample_rate=22050`,
    '-t', (durationMs / 1000).toFixed(3),
    '-c:a', 'pcm_s16le',
    `/work/${name}`,
  ];
  const r = spawnSync('docker', args, { stdio: ['ignore', 'ignore', 'pipe'] });
  if (r.status !== 0) throw new Error(`silence gen failed: ${r.stderr}`);
  return { kind: 'silence', name, durationMs };
}

for (const section of sections) {
  const gap = section.startMs - cursorMs;
  if (gap > 0) {
    const s = genSilence(gap);
    if (s) concatItems.push(s);
    cursorMs += gap;
  }
  const wavMs = probeDurationMs(join(WORK_DIR, `${section.id}.wav`));
  concatItems.push({ kind: 'section', name: `${section.id}.wav`, durationMs: wavMs });
  cursorMs += wavMs;
}

// Pad to video length if narration is shorter.
if (cursorMs < videoMs) {
  const s = genSilence(videoMs - cursorMs);
  if (s) concatItems.push(s);
  cursorMs = videoMs;
}

// ──────────────────────────────────────────────────────────────────────
// 4. Write concat list, ffmpeg-concat into a single track.
// ──────────────────────────────────────────────────────────────────────

const concatList = concatItems.map(i => `file '${i.name}'`).join('\n') + '\n';
writeFileSync(join(WORK_DIR, 'concat.txt'), concatList);

const concatArgs = [
  'run', '--rm',
  '-v', `${WORK_DIR}:/work`,
  '-w', '/work',
  '--entrypoint', '/usr/local/bin/ffmpeg',
  'linuxserver/ffmpeg:latest',
  '-y',
  '-f', 'concat', '-safe', '0',
  '-i', 'concat.txt',
  '-c:a', 'aac', '-b:a', '128k',
  'narration.m4a',
];
let r = spawnSync('docker', concatArgs, { stdio: 'inherit' });
if (r.status !== 0) throw new Error('ffmpeg audio concat failed');

const finalAudioMs = probeDurationMs(join(WORK_DIR, 'narration.m4a'));
console.log(`[narration] assembled audio: ${(finalAudioMs / 1000).toFixed(1)}s (video: ${(videoMs / 1000).toFixed(1)}s)`);

// ──────────────────────────────────────────────────────────────────────
// 5. Mux audio onto the video.
// ──────────────────────────────────────────────────────────────────────

// Stage the video into WORK_DIR so the docker mount path is consistent.
const stagedVideo = join(WORK_DIR, 'demo.mp4');
copyFileSync(INPUT_VIDEO, stagedVideo);

const muxArgs = [
  'run', '--rm',
  '-v', `${WORK_DIR}:/work`,
  '-w', '/work',
  '--entrypoint', '/usr/local/bin/ffmpeg',
  'linuxserver/ffmpeg:latest',
  '-y',
  '-i', 'demo.mp4',
  '-i', 'narration.m4a',
  '-map', '0:v', '-map', '1:a',
  '-c:v', 'copy', '-c:a', 'copy',
  '-shortest',
  'demo-narrated.mp4',
];
r = spawnSync('docker', muxArgs, { stdio: 'inherit' });
if (r.status !== 0) throw new Error('ffmpeg mux failed');

// Copy the final file to the demo-output dir.
copyFileSync(join(WORK_DIR, 'demo-narrated.mp4'), OUTPUT);

const finalStat = statSync(OUTPUT);
console.log(`\n[narration] wrote ${OUTPUT} (${(finalStat.size / 1024 / 1024).toFixed(1)} MiB)`);
