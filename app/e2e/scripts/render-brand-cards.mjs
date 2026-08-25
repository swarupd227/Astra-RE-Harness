#!/usr/bin/env node
/**
 * Render branded intro/outro cards (Artizent logo + copy) as standalone
 * .mp4 clips, matching the exact codec/resolution/fps conventions used by
 * trim-demo.mjs so they concat losslessly (-c copy) with a trimmed demo:
 *   1600x1000, 25fps, libx264, yuv420p, preset medium, crf 20, no audio.
 *
 * Usage:
 *   node scripts/render-brand-cards.mjs
 *
 * Writes intro.mp4 + outro.mp4 into demo-output/branding/, using the logo
 * at demo-output/branding/artizent-logo.png (real alpha transparency,
 * verified via ffprobe/signalstats before this script was written — safe
 * to composite directly onto a dark background, no chroma-key needed).
 */
import { existsSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const E2E_ROOT = resolve(__dirname, '..');
const BRAND_DIR = join(E2E_ROOT, 'demo-output', 'branding');
const LOGO_NAME = 'artizent-logo.png';

const W = 1600, H = 1000, FPS = 25;
const NAVY = '0x0F172A';   // matches the live on-screen caption banner (rgba(15,23,42,.96))
const GOLD = '0xD4A72C';   // pulled from the logo's own amber/gold glyph
const WHITE = '0xF8FAFC';
const SLATE = '0xCBD5E1';
const SLATE_DIM = '0x94A3B8';

const FONT_BOLD = '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf';
const FONT_REG = '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf';

if (!existsSync(join(BRAND_DIR, LOGO_NAME))) {
  throw new Error(`Missing ${join(BRAND_DIR, LOGO_NAME)} — copy the Artizent logo there first.`);
}

function escapeDrawText(s) {
  return s
    .replace(/\\/g, '\\\\')
    .replace(/'/g, "\\'")
    .replace(/:/g, '\\:')
    .replace(/,/g, '\\,');
}

function dockerFFmpeg(args) {
  const dockerArgs = [
    'run', '--rm',
    '-v', `${BRAND_DIR}:/work`,
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

function drawText(text, { font = FONT_REG, size, color = WHITE, y }) {
  return `drawtext=fontfile=${font}:text='${escapeDrawText(text)}':fontcolor=${color}:fontsize=${size}:x=(w-text_w)/2:y=${y}`;
}

/**
 * @param {object} opts
 * @param {string} opts.outName
 * @param {number} opts.duration
 * @param {string[]} opts.lines - drawtext filter strings, applied in order after the logo overlay
 */
function renderCard({ outName, duration, lines }) {
  // The logo file already bakes in the "ARTIZENT" wordmark beneath the
  // glyph, so no separate wordmark text is drawn here — that would just
  // repeat what's already in the image.
  const logoTargetW = 460; // px, out of 1600 canvas width
  const logoY = 90;

  const filterComplex = [
    `[1:v]scale=${logoTargetW}:-1[logo]`,
    `[0:v][logo]overlay=(W-w)/2:${logoY}[bg0]`,
    // Thin gold rule beneath the logo block, echoes the logo's own colour.
    `[bg0]drawbox=x=(iw-360)/2:y=${logoY + 210}:w=360:h=3:color=${GOLD}:t=fill[bg1]`,
    ...lines.map((l, i) => `[bg${i + 1}]${l}[bg${i + 2}]`),
  ];
  // Fix up the chain's final label.
  const lastLabel = `bg${lines.length + 1}`;
  filterComplex[filterComplex.length - 1] = filterComplex[filterComplex.length - 1].replace(
    new RegExp(`\\[${lastLabel}\\]$`), '[vout]',
  );

  dockerFFmpeg([
    '-y',
    '-f', 'lavfi', '-i', `color=c=${NAVY}:s=${W}x${H}:r=${FPS}:d=${duration}`,
    '-i', LOGO_NAME,
    '-filter_complex', filterComplex.join(';'),
    '-map', '[vout]',
    '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-preset', 'medium', '-crf', '20',
    '-an',
    outName,
  ]);
}

mkdirSync(BRAND_DIR, { recursive: true });

console.log('[render-brand-cards] rendering intro.mp4 …');
renderCard({
  outName: 'intro.mp4',
  duration: 5,
  lines: [
    drawText('presents', { font: FONT_REG, size: 26, color: SLATE_DIM, y: 340 }),
    drawText('Astra RE Harness', { font: FONT_BOLD, size: 70, color: WHITE, y: 400 }),
    drawText('An Agentic platform for reverse-engineering legacy code', { font: FONT_REG, size: 30, color: SLATE, y: 520 }),
    drawText('and migrating it to modern platforms.', { font: FONT_REG, size: 30, color: SLATE, y: 565 }),
  ],
});

console.log('[render-brand-cards] rendering outro.mp4 …');
renderCard({
  outName: 'outro.mp4',
  duration: 5,
  lines: [
    drawText('Agentic Reverse Engineering & Migration', { font: FONT_REG, size: 32, color: SLATE, y: 360 }),
    drawText('Learn more at', { font: FONT_REG, size: 26, color: SLATE_DIM, y: 500 }),
    drawText('www.artizent.com', { font: FONT_BOLD, size: 54, color: GOLD, y: 545 }),
  ],
});

console.log(`[render-brand-cards] wrote ${join(BRAND_DIR, 'intro.mp4')} + outro.mp4`);
