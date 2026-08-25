/**
 * Astra RE Harness · Delphi & C++ Migration · Technical Solution deck.
 *
 * Builds a 16-slide PPTX explaining (a) the end-to-end pipeline in plain language,
 * (b) the engineering algorithms behind each stage, and (c) where Nous adds value
 * above a raw LLM. All flow diagrams use native PowerPoint shapes — no images.
 */
const pptxgen = require('pptxgenjs');

const pres = new pptxgen();
pres.layout = 'LAYOUT_WIDE';     // 13.3" × 7.5" — more room for diagrams
pres.title  = 'Astra RE Harness — Delphi & C++ Technical Solution';
pres.author = 'Nous';

// ── Palette ───────────────────────────────────────────────────────────────
const C = {
  bgDark:   '1E1B4B',  // deep indigo
  bgLight:  'F8FAFC',  // off-white
  text:     '0F172A',  // slate-900
  muted:    '64748B',  // slate-500
  border:   'CBD5E1',  // slate-300
  primary:  '6366F1',  // indigo-500
  primaryDk:'4338CA',  // indigo-700
  ok:       '10B981',  // emerald
  warn:     'F59E0B',  // amber
  white:    'FFFFFF',
  delphi:   '10B981',  // emerald — Delphi accent
  cpp:      'F59E0B',  // amber — C++ accent
  cobol:    '14B8A6',  // teal — COBOL accent
  fortran:  '6366F1',  // indigo — Fortran accent
};

const F = {
  head: 'Calibri',
  body: 'Calibri',
};

// ── Shared helpers ────────────────────────────────────────────────────────
function pageHeader(slide, eyebrow, title) {
  slide.background = { color: C.bgLight };
  // Eyebrow chip — width sized to the label so wide words like
  // "DIFFERENTIATION" don't wrap.
  const ew = Math.max(1.6, 0.13 * eyebrow.length + 0.5);
  slide.addShape(pres.shapes.RECTANGLE, {
    x: 0.5, y: 0.35, w: ew, h: 0.3,
    fill: { color: C.primary }, line: { type: 'none' },
  });
  slide.addText(eyebrow, {
    x: 0.5, y: 0.35, w: ew, h: 0.3,
    fontSize: 10, fontFace: F.head, color: C.white, bold: true,
    align: 'center', valign: 'middle', margin: 0, charSpacing: 3,
  });
  // Title
  slide.addText(title, {
    x: 0.5, y: 0.7, w: 12.3, h: 0.7,
    fontSize: 30, fontFace: F.head, color: C.text, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  // Hairline divider
  slide.addShape(pres.shapes.RECTANGLE, {
    x: 0.5, y: 1.45, w: 12.3, h: 0.02,
    fill: { color: C.border }, line: { type: 'none' },
  });
  // Footer brand
  slide.addText('Astra RE Harness  ·  Phase 9 — Delphi + C++  ·  Nous', {
    x: 0.5, y: 7.1, w: 12.3, h: 0.3,
    fontSize: 9, fontFace: F.body, color: C.muted,
    align: 'left', valign: 'middle', margin: 0,
  });
}

function arrowRight(slide, x, y, w, color) {
  // Horizontal arrow using a Line with end-arrow.
  slide.addShape(pres.shapes.LINE, {
    x, y, w, h: 0,
    line: { color: color || C.primary, width: 3, endArrowType: 'triangle' },
  });
}

function arrowDown(slide, x, y, h, color) {
  slide.addShape(pres.shapes.LINE, {
    x, y, w: 0, h,
    line: { color: color || C.primary, width: 3, endArrowType: 'triangle' },
  });
}

function flowBox(slide, x, y, w, h, num, label, sublabel, color) {
  const fill = color || C.primary;
  slide.addShape(pres.shapes.RECTANGLE, {
    x, y, w, h,
    fill: { color: C.white },
    line: { color: fill, width: 1.5 },
    shadow: { type: 'outer', color: '000000', blur: 8, offset: 2, angle: 90, opacity: 0.08 },
  });
  // Numbered cap
  slide.addShape(pres.shapes.RECTANGLE, {
    x, y, w, h: 0.32,
    fill: { color: fill }, line: { type: 'none' },
  });
  slide.addText(num, {
    x, y, w, h: 0.32,
    fontSize: 11, fontFace: F.head, color: C.white, bold: true,
    align: 'center', valign: 'middle', margin: 0, charSpacing: 2,
  });
  // Label
  slide.addText(label, {
    x: x + 0.1, y: y + 0.4, w: w - 0.2, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'center', valign: 'middle', margin: 0,
  });
  // Sublabel
  slide.addText(sublabel, {
    x: x + 0.1, y: y + 0.85, w: w - 0.2, h: h - 0.95,
    fontSize: 10, fontFace: F.body, color: C.muted,
    align: 'center', valign: 'top', margin: 0,
  });
}

function valueCard(slide, x, y, w, h, accent, heading, body) {
  slide.addShape(pres.shapes.RECTANGLE, {
    x, y, w, h,
    fill: { color: C.white }, line: { color: C.border, width: 1 },
    shadow: { type: 'outer', color: '000000', blur: 10, offset: 3, angle: 90, opacity: 0.08 },
  });
  slide.addShape(pres.shapes.RECTANGLE, {
    x, y, w: 0.12, h,
    fill: { color: accent }, line: { type: 'none' },
  });
  slide.addText(heading, {
    x: x + 0.35, y: y + 0.18, w: w - 0.5, h: 0.45,
    fontSize: 16, fontFace: F.head, color: C.text, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  slide.addText(body, {
    x: x + 0.35, y: y + 0.68, w: w - 0.5, h: h - 0.85,
    fontSize: 11.5, fontFace: F.body, color: C.muted,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 4,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 1 — Title
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  s.background = { color: C.bgDark };

  // Accent stripe
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0, y: 0, w: 0.18, h: 7.5,
    fill: { color: C.primary }, line: { type: 'none' },
  });

  // Brand block
  s.addText('ASTRA RE HARNESS', {
    x: 0.7, y: 0.9, w: 8, h: 0.4,
    fontSize: 12, fontFace: F.head, color: C.primary, bold: true,
    align: 'left', margin: 0, charSpacing: 8,
  });
  s.addText('Delphi & C++ Migration', {
    x: 0.7, y: 1.4, w: 12, h: 1.1,
    fontSize: 54, fontFace: F.head, color: C.white, bold: true,
    align: 'left', margin: 0,
  });
  s.addText('Technical Solution Brief', {
    x: 0.7, y: 2.55, w: 12, h: 0.6,
    fontSize: 28, fontFace: F.head, color: 'CADCFC', italic: true,
    align: 'left', margin: 0,
  });
  s.addText('How the harness turns legacy source into a buildable, audit-grade .NET 8 package — and what Nous adds on top of a raw language model.', {
    x: 0.7, y: 3.4, w: 11.5, h: 1.0,
    fontSize: 16, fontFace: F.body, color: 'CADCFC',
    align: 'left', valign: 'top', margin: 0,
  });

  // Four language squares with corresponding accent dots
  const langs = [
    { label: 'Fortran', color: C.fortran },
    { label: 'COBOL',   color: C.cobol  },
    { label: 'Delphi',  color: C.delphi },
    { label: 'C++',     color: C.cpp    },
  ];
  langs.forEach((L, i) => {
    const x = 0.7 + i * 2.1;
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 5.4, w: 1.85, h: 1.2,
      fill: { color: '283466' }, line: { color: L.color, width: 2 },
    });
    s.addShape(pres.shapes.OVAL, {
      x: x + 0.18, y: 5.58, w: 0.32, h: 0.32,
      fill: { color: L.color }, line: { type: 'none' },
    });
    s.addText(L.label, {
      x: x + 0.55, y: 5.55, w: 1.2, h: 0.4,
      fontSize: 16, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(i < 2 ? 'shipped' : 'NEW — Phase 9', {
      x: x + 0.18, y: 6.05, w: 1.6, h: 0.4,
      fontSize: 10, fontFace: F.body,
      color: i < 2 ? 'CADCFC' : L.color,
      bold: i >= 2, italic: i < 2,
      align: 'left', valign: 'middle', margin: 0,
    });
  });

  s.addText('Source: Astra RE Harness · Phase 9.6 demo (Indy TIdSMTP.Connect, fmt::format)', {
    x: 0.7, y: 6.9, w: 11.5, h: 0.3,
    fontSize: 10, fontFace: F.body, color: '94A3B8', italic: true,
    align: 'left', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 2 — Executive Summary (Problem / Approach / Outcome)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'EXEC SUMMARY', 'Why this matters, in one slide');

  const cols = [
    {
      title: 'The Problem',
      color: 'EF4444',
      body: [
        'Legacy Delphi + C++ codebases run mission-critical workloads.',
        'Every line is institutional knowledge. A bare LLM rewrite loses citations, drops invariants, and ships untested code.',
        'Manual migration costs years and dozens of FTEs — and stalls the cloud roadmap.',
      ],
    },
    {
      title: 'Our Approach',
      color: C.primary,
      body: [
        'Treat migration as a pipeline: parse → spec → SME sign → scaffold → validate.',
        'Each stage is auditable. Every claim cites the exact source lines it came from.',
        'The harness combines language-specific tooling with Claude — neither alone is enough.',
      ],
    },
    {
      title: 'The Outcome',
      color: C.ok,
      body: [
        'A buildable .NET 8 package with xUnit tests, traced to a signed spec.',
        'Four independent gates verify correctness — including property-based search.',
        'A wave-by-wave migration plan that respects dependencies and risk.',
      ],
    },
  ];

  cols.forEach((col, i) => {
    const x = 0.5 + i * 4.2;
    valueCard(s, x, 1.8, 4.0, 5.0, col.color, col.title, col.body.join('\n\n'));
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 3 — End-to-end pipeline flow
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'PIPELINE', 'End-to-end: from legacy source to validated .NET 8 package');

  const steps = [
    { num: 'STEP 1', label: 'Ingest',  sub: 'Git URL or zip\nupload',                color: C.primary },
    { num: 'STEP 2', label: 'Parse',   sub: 'Language-specific\nAST + routine catalog', color: C.primary },
    { num: 'STEP 3', label: 'Extract', sub: 'Claude → contract spec\nwith citations', color: C.primary },
    { num: 'STEP 4', label: 'Sign',    sub: 'SME reviews every\nclaim · evidence',     color: C.primary },
    { num: 'STEP 5', label: 'Scaffold',sub: 'Archetype + Claude →\n.NET 8 source',   color: C.primary },
    { num: 'STEP 6', label: 'Validate',sub: '4 gates — compile,\ntest, equivalence, property', color: C.ok    },
  ];

  const boxW = 1.85, boxH = 1.7, gap = 0.18;
  const totalW = boxW * 6 + gap * 5;
  const startX = (13.3 - totalW) / 2;
  const yRow = 2.2;

  steps.forEach((st, i) => {
    const x = startX + i * (boxW + gap);
    flowBox(s, x, yRow, boxW, boxH, st.num, st.label, st.sub, st.color);
    if (i < steps.length - 1) {
      arrowRight(s, x + boxW + 0.01, yRow + boxH / 2, gap - 0.02, C.primary);
    }
  });

  // Persona lanes underneath
  const lane = (label, x, w, color) => {
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, {
      x, y: 4.3, w, h: 0.5,
      fill: { color }, line: { type: 'none' },
      rectRadius: 0.05,
    });
    s.addText(label, {
      x, y: 4.3, w, h: 0.5,
      fontSize: 11, fontFace: F.head, color: C.white, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
  };
  const sx = startX;
  lane('Engineer',     sx,                                boxW * 3 + gap * 3,     C.primary);
  lane('SME',          sx + boxW * 3 + gap * 3 + 0.05,    boxW + gap * 2 - 0.05,  '8B5CF6');
  lane('Engineer',     sx + boxW * 4 + gap * 5 - 0.10,    boxW + gap * 2 - 0.10,  C.primary);
  lane('Harness auto', sx + boxW * 5 + gap * 7 - 0.16,    boxW + gap * 2 - 0.02,  C.ok);

  s.addText('Who does what at each stage', {
    x: 0.5, y: 4.95, w: 12.3, h: 0.3,
    fontSize: 11, fontFace: F.body, color: C.muted, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });

  // Key takeaway band
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0.5, y: 5.55, w: 12.3, h: 1.1,
    fill: { color: '1E293B' }, line: { type: 'none' },
  });
  s.addText('Every stage is auditable, idempotent, and rerunnable.', {
    x: 0.7, y: 5.62, w: 12.0, h: 0.45,
    fontSize: 18, fontFace: F.head, color: C.white, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText('The pipeline is not a chat — it\'s a verifiable state machine. A migration that survives audits, not just one that compiles.', {
    x: 0.7, y: 6.05, w: 12.0, h: 0.5,
    fontSize: 12, fontFace: F.body, color: 'CADCFC',
    align: 'left', valign: 'top', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 4 — What Nous adds on top of a raw LLM
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'DIFFERENTIATION', 'Where Nous adds value — beyond an LLM call');

  const props = [
    {
      title: 'Language-aware parsing',
      body: 'A raw LLM reads source as text. We feed Claude a real AST from tree-sitter-pascal (Delphi) or libclang (C++). The model never has to guess what a routine boundary is — it sees structured input.',
      color: C.primary,
    },
    {
      title: 'Citation-grounded specs',
      body: 'Every claim in the contract spec carries source-line citations. The SME can verify what Claude said against the original Pascal or C++ on the same screen. No hallucination is unchecked.',
      color: C.delphi,
    },
    {
      title: 'Archetype-driven scaffolds',
      body: 'Output isn\'t free-form. The mock provider lays down a buildable archetype (project + tests + interfaces) and the LLM fills typed gaps. Result: a scaffold that always compiles.',
      color: C.cpp,
    },
    {
      title: 'Four-gate validation',
      body: 'Compile, test pack, cross-runtime equivalence, and property-based search. Each gate is independent; all four must pass. Property search actively hunts inputs that break behaviour parity.',
      color: C.ok,
    },
  ];

  const cw = 6.0, ch = 2.4;
  props.forEach((p, i) => {
    const col = i % 2, row = Math.floor(i / 2);
    const x = 0.5 + col * (cw + 0.3);
    const y = 1.85 + row * (ch + 0.3);
    valueCard(s, x, y, cw, ch, p.color, p.title, p.body);
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 5 — Stage 1 + 2: Parse → spec extraction
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'STAGE 1 · PARSE', 'Language-aware AST — not regex, not "vibes"');

  // Left: explanation
  s.addText('In plain language:', {
    x: 0.5, y: 1.8, w: 6, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'left', margin: 0,
  });
  s.addText(
    'We read every source file with a real parser that knows the language\'s grammar. ' +
    'The parser produces a structured tree of every routine, every type, and every call relationship. ' +
    'Only then do we hand things over to the LLM.',
    {
      x: 0.5, y: 2.2, w: 5.8, h: 1.6,
      fontSize: 13, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 8,
    });

  s.addText('Why it matters:', {
    x: 0.5, y: 4.0, w: 6, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'left', margin: 0,
  });
  s.addText([
    { text: 'Routine boundaries are exact (no fuzzy regex)', options: { bullet: true, breakLine: true } },
    { text: 'Call graphs feed Stage 5 — migration planning', options: { bullet: true, breakLine: true } },
    { text: 'Citation line numbers are precise — SMEs trust them', options: { bullet: true } },
  ], {
    x: 0.5, y: 4.4, w: 5.8, h: 2.3,
    fontSize: 12.5, fontFace: F.body, color: C.text,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 6,
  });

  // Right: side-by-side language tooling
  const cardX = 7.0, cardW = 5.8;

  // Delphi card
  s.addShape(pres.shapes.RECTANGLE, {
    x: cardX, y: 1.85, w: cardW, h: 2.4,
    fill: { color: C.white }, line: { color: C.delphi, width: 2 },
  });
  s.addShape(pres.shapes.RECTANGLE, {
    x: cardX, y: 1.85, w: cardW, h: 0.4,
    fill: { color: C.delphi }, line: { type: 'none' },
  });
  s.addText('Delphi  ·  tree-sitter-pascal', {
    x: cardX + 0.15, y: 1.85, w: cardW - 0.3, h: 0.4,
    fontSize: 13, fontFace: F.head, color: C.white, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText([
    { text: 'In-process — no subprocess, no temp files',                       options: { bullet: true, breakLine: true } },
    { text: 'Recognises: TComponent, ARC interfaces, properties, events, RTTI', options: { bullet: true, breakLine: true } },
    { text: 'Reads .pas + .dpr; resolves uses-clause across the corpus',        options: { bullet: true, breakLine: true } },
    { text: 'Tested on 60k LOC Indy Sockets seed',                              options: { bullet: true } },
  ], {
    x: cardX + 0.2, y: 2.35, w: cardW - 0.35, h: 1.85,
    fontSize: 11.5, fontFace: F.body, color: C.text,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 5,
  });

  // C++ card
  s.addShape(pres.shapes.RECTANGLE, {
    x: cardX, y: 4.4, w: cardW, h: 2.4,
    fill: { color: C.white }, line: { color: C.cpp, width: 2 },
  });
  s.addShape(pres.shapes.RECTANGLE, {
    x: cardX, y: 4.4, w: cardW, h: 0.4,
    fill: { color: C.cpp }, line: { type: 'none' },
  });
  s.addText('C++  ·  libclang AST', {
    x: cardX + 0.15, y: 4.4, w: cardW - 0.3, h: 0.4,
    fontSize: 13, fontFace: F.head, color: C.white, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText([
    { text: 'Runs the real Clang frontend — handles templates, lambdas, C++20', options: { bullet: true, breakLine: true } },
    { text: 'Catches: template instantiations, UB markers, exception specs',     options: { bullet: true, breakLine: true } },
    { text: 'Honours preprocessor branches (FMT_USE_CONSTEXPR, etc.)',           options: { bullet: true, breakLine: true } },
    { text: 'Tested on 14k LOC fmtlib seed',                                     options: { bullet: true } },
  ], {
    x: cardX + 0.2, y: 4.9, w: cardW - 0.35, h: 1.85,
    fontSize: 11.5, fontFace: F.body, color: C.text,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 5,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 6 — Spec extraction (Stage 3)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'STAGE 2 · EXTRACT', 'How a contract spec is born — and why each claim is cited');

  // Top: horizontal data-flow diagram
  // [Source] → [Neighbourhood] → [Prompt] → [Claude] → [JSON spec] → [Schema validator]
  const blocks = [
    { label: 'Source\nfile',          sub: 'Pascal / C++' },
    { label: 'Routine +\nneighbours', sub: 'callees, siblings, types' },
    { label: 'Prompt\nbundle',        sub: 'system + user + schema' },
    { label: 'Claude\nSonnet',        sub: 'streaming JSON' },
    { label: 'Validate +\npersist',   sub: 'schema + claim-id audit' },
  ];

  const bw = 2.1, bh = 1.5, gap = 0.18;
  const totalW = bw * blocks.length + gap * (blocks.length - 1);
  const sx = (13.3 - totalW) / 2;
  const yRow = 1.85;

  blocks.forEach((b, i) => {
    const x = sx + i * (bw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: yRow, w: bw, h: bh,
      fill: { color: C.white }, line: { color: C.primary, width: 1.5 },
      shadow: { type: 'outer', color: '000000', blur: 8, offset: 2, angle: 90, opacity: 0.08 },
    });
    s.addText(b.label, {
      x: x + 0.1, y: yRow + 0.2, w: bw - 0.2, h: 0.7,
      fontSize: 14, fontFace: F.head, color: C.text, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(b.sub, {
      x: x + 0.1, y: yRow + 0.95, w: bw - 0.2, h: 0.5,
      fontSize: 10, fontFace: F.body, color: C.muted, italic: true,
      align: 'center', valign: 'top', margin: 0,
    });
    if (i < blocks.length - 1) {
      arrowRight(s, x + bw + 0.01, yRow + bh / 2, gap - 0.02, C.primary);
    }
  });

  // Bottom: claim taxonomy (5 pills for Delphi-specific kinds)
  s.addText('Each spec is taxonomy-typed — 5 Delphi-specific claim kinds (3 more for C++):', {
    x: 0.5, y: 3.7, w: 12.3, h: 0.4,
    fontSize: 13, fontFace: F.head, color: C.text, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });

  const claims = [
    { label: 'invariants',           desc: 'must always hold' },
    { label: 'object_lifetime',      desc: 'ARC, Free, destructor order' },
    { label: 'interface_implementation', desc: 'IInterface contract surface' },
    { label: 'property_accessor',    desc: 'getter / setter / default behaviour' },
    { label: 'event_handler_contract', desc: 'when fires, what args' },
    { label: 'rtti_usage',           desc: 'runtime type queries' },
    { label: 'side_effects',         desc: 'I/O, mutation, exceptions' },
    { label: 'edge_cases',           desc: 'inputs that twist the behaviour' },
    { label: 'open_questions',       desc: 'unknowns SME must resolve' },
  ];

  const pw = 1.95, ph = 0.7, pgap = 0.10;
  const cols = 5;
  claims.forEach((c, i) => {
    const col = i % cols, row = Math.floor(i / cols);
    const x = 0.5 + col * (pw + pgap);
    const y = 4.25 + row * (ph + 0.18);
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, {
      x, y, w: pw, h: ph,
      fill: { color: 'EEF2FF' }, line: { color: C.primaryDk, width: 0.75 },
      rectRadius: 0.06,
    });
    s.addText(c.label, {
      x: x + 0.1, y: y + 0.04, w: pw - 0.2, h: 0.3,
      fontSize: 9, fontFace: F.head, color: C.primaryDk, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(c.desc, {
      x: x + 0.1, y: y + 0.35, w: pw - 0.2, h: 0.32,
      fontSize: 8.5, fontFace: F.body, color: C.muted, italic: true,
      align: 'left', valign: 'middle', margin: 0,
    });
  });

  s.addText('Citation rule: every claim must reference at least one source-line range. Specs that fail this check are rejected before the SME ever sees them.', {
    x: 0.5, y: 6.6, w: 12.3, h: 0.5,
    fontSize: 11, fontFace: F.body, color: C.muted, italic: true,
    align: 'left', valign: 'top', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 7 — SME Review + Sign (state machine)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'STAGE 3 · SIGN', 'SME review workflow — every claim accepted on the record');

  // States as bubbles
  const state = (x, y, w, h, label, sub, color, fill) => {
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, {
      x, y, w, h,
      fill: { color: fill || C.white }, line: { color, width: 2 },
      rectRadius: 0.08,
    });
    s.addText(label, {
      x: x + 0.1, y: y + 0.15, w: w - 0.2, h: 0.4,
      fontSize: 15, fontFace: F.head, color, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(sub, {
      x: x + 0.1, y: y + 0.6, w: w - 0.2, h: h - 0.7,
      fontSize: 10.5, fontFace: F.body, color: C.muted,
      align: 'center', valign: 'middle', margin: 0,
    });
  };

  const sw = 2.2, sh = 1.4;
  const row = 2.4;
  state(0.7,  row, sw, sh, 'DRAFT',      'Claude wrote the\nspec; engineer reviews', C.primary);
  state(3.85, row, sw, sh, 'IN_REVIEW',  'SME sees claims +\ncitations side-by-side', '8B5CF6');
  state(7.0,  row, sw, sh, 'SIGNED',     'Every claim accepted\nor resolved on the record', C.ok);
  state(10.15,row, sw, sh, 'SCAFFOLDED', 'Engineer triggers .NET 8\nscaffold from signed spec', C.warn);

  // Arrows between states
  const arrowLabel = (x, y, w, text, color) => {
    arrowRight(s, x, y, w, color);
    s.addText(text, {
      x: x - 0.05, y: y - 0.42, w: w + 0.1, h: 0.35,
      fontSize: 10, fontFace: F.body, color, italic: true,
      align: 'center', valign: 'middle', margin: 0,
    });
  };
  arrowLabel(2.9,  row + sh / 2, 0.95, 'Route to SME', C.muted);
  arrowLabel(6.05, row + sh / 2, 0.95, 'Sign-off',     C.muted);
  arrowLabel(9.20, row + sh / 2, 0.95, 'Generate',     C.muted);

  // Side note — what happens during IN_REVIEW
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0.7, y: 4.5, w: 12, h: 2.2,
    fill: { color: '1E293B' }, line: { type: 'none' },
  });
  s.addText('What happens during IN_REVIEW', {
    x: 0.9, y: 4.6, w: 11.5, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.white, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText([
    { text: 'For each claim, the SME chooses Accept, Edit, or Resolve (for open questions).', options: { bullet: true, breakLine: true } },
    { text: 'The cited source lines appear in a side panel — no context switching.',           options: { bullet: true, breakLine: true } },
    { text: 'Sign-off requires every claim to be processed. The /sign endpoint rejects partial reviews.', options: { bullet: true, breakLine: true } },
    { text: 'Every action — accept, edit, comment — is written to an immutable audit log.',     options: { bullet: true } },
  ], {
    x: 0.9, y: 5.05, w: 11.5, h: 1.55,
    fontSize: 11.5, fontFace: F.body, color: 'CADCFC',
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 4,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 8 — Scaffold (archetype pattern)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'STAGE 4 · SCAFFOLD', 'Archetypes — the reusable blueprint behind every package');

  s.addText('Without archetypes, an LLM writes free-form code that often fails to compile. With archetypes, the harness lays down a buildable skeleton — interfaces, project file, test fixtures — and Claude only fills typed gaps.', {
    x: 0.5, y: 1.85, w: 12.3, h: 0.9,
    fontSize: 13.5, fontFace: F.body, color: C.muted,
    align: 'left', valign: 'top', margin: 0,
  });

  // Pipeline blocks
  const bw = 2.3, bh = 1.5, gap = 0.25;
  const items = [
    { label: 'Signed spec',         sub: 'claims + citations',         color: C.ok },
    { label: 'Archetype manifest',  sub: 'canonical-delphi-idsmtp\nor canonical-cpp-fmt', color: C.primary },
    { label: 'Scaffold pipeline',   sub: 'mock provider · streaming',  color: C.primary },
    { label: '.NET 8 package',      sub: '6 files · csproj · xUnit',   color: C.warn },
  ];

  const totalW = bw * items.length + gap * (items.length - 1);
  const sx = (13.3 - totalW) / 2;
  const yRow = 3.0;
  items.forEach((it, i) => {
    const x = sx + i * (bw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: yRow, w: bw, h: bh,
      fill: { color: C.white }, line: { color: it.color, width: 2 },
      shadow: { type: 'outer', color: '000000', blur: 10, offset: 3, angle: 90, opacity: 0.10 },
    });
    s.addText(it.label, {
      x: x + 0.1, y: yRow + 0.25, w: bw - 0.2, h: 0.5,
      fontSize: 14, fontFace: F.head, color: it.color, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(it.sub, {
      x: x + 0.1, y: yRow + 0.8, w: bw - 0.2, h: 0.65,
      fontSize: 11, fontFace: F.body, color: C.muted,
      align: 'center', valign: 'top', margin: 0,
    });
    if (i < items.length - 1) {
      arrowRight(s, x + bw + 0.02, yRow + bh / 2, gap - 0.04, C.muted);
    }
  });

  // What's in an archetype
  s.addText('What\'s in a Phase 9 archetype', {
    x: 0.5, y: 5.0, w: 12.3, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText([
    { text: 'Project file (.csproj) — net8.0 target, nullable on, source-link enabled', options: { bullet: true, breakLine: true } },
    { text: 'Source files with [SpecClaim("INV-1")] attributes tying code back to spec', options: { bullet: true, breakLine: true } },
    { text: 'Test pack (xUnit) — one [Fact] per invariant; Theory rows from edge_cases',  options: { bullet: true, breakLine: true } },
    { text: 'matches.anyOf — which routine names the archetype claims; harness picks automatically',   options: { bullet: true } },
  ], {
    x: 0.5, y: 5.4, w: 12.3, h: 1.7,
    fontSize: 12, fontFace: F.body, color: C.text,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 5,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 9 — Four-gate validation
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'STAGE 5 · VALIDATE', 'Four independent gates — all four must pass');

  const gates = [
    {
      n: 'GATE 1', label: 'Compile',
      desc: 'dotnet build the generated package. Fast (~5s). Catches type errors, missing usings, broken references.',
      algo: 'dotnet SDK 8.0',
      color: C.primary,
    },
    {
      n: 'GATE 2', label: 'Test pack',
      desc: 'Run xUnit. Each spec invariant becomes a [Fact]. Spec edge_cases become Theory rows. ~3–4s.',
      algo: 'xUnit + Verify',
      color: C.primary,
    },
    {
      n: 'GATE 3', label: 'Equivalence',
      desc: 'Run identical inputs through both runtimes. Outputs must match. Catches drift the test pack missed.',
      algo: 'fpc-sidecar (Delphi)\ngpp-sidecar (C++)',
      color: C.delphi,
    },
    {
      n: 'GATE 4', label: 'Falsifying',
      desc: 'Hypothesis-based property search hunts inputs that make the runtimes disagree. ~17s budget, 100 tries.',
      algo: 'property-test-sidecar',
      color: C.cpp,
    },
  ];

  const cw = 3.0, ch = 3.6, gap = 0.18;
  const totalW = cw * 4 + gap * 3;
  const sx = (13.3 - totalW) / 2;
  const yRow = 1.85;

  gates.forEach((g, i) => {
    const x = sx + i * (cw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: yRow, w: cw, h: ch,
      fill: { color: C.white }, line: { color: g.color, width: 1.5 },
      shadow: { type: 'outer', color: '000000', blur: 10, offset: 3, angle: 90, opacity: 0.10 },
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: yRow, w: cw, h: 0.4,
      fill: { color: g.color }, line: { type: 'none' },
    });
    s.addText(g.n, {
      x: x + 0.15, y: yRow, w: cw - 0.3, h: 0.4,
      fontSize: 10.5, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0, charSpacing: 3,
    });
    s.addText(g.label, {
      x: x + 0.15, y: yRow + 0.55, w: cw - 0.3, h: 0.5,
      fontSize: 22, fontFace: F.head, color: C.text, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(g.desc, {
      x: x + 0.15, y: yRow + 1.15, w: cw - 0.3, h: 1.8,
      fontSize: 11.5, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });
    // Algo chip
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, {
      x: x + 0.15, y: yRow + 2.9, w: cw - 0.3, h: 0.55,
      fill: { color: 'EEF2FF' }, line: { type: 'none' },
      rectRadius: 0.06,
    });
    s.addText(g.algo, {
      x: x + 0.2, y: yRow + 2.9, w: cw - 0.4, h: 0.55,
      fontSize: 10.5, fontFace: F.head, color: C.primaryDk, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
  });

  // Closing band
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0.5, y: 5.75, w: 12.3, h: 1.0,
    fill: { color: '1E293B' }, line: { type: 'none' },
  });
  s.addText('Why four? — each gate finds a class the others miss.', {
    x: 0.7, y: 5.8, w: 12.0, h: 0.4,
    fontSize: 15, fontFace: F.head, color: C.white, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText('Compile catches syntax errors. Tests catch known invariants. Equivalence catches drift on the same inputs. Property search finds inputs you never thought to try.', {
    x: 0.7, y: 6.2, w: 12.0, h: 0.5,
    fontSize: 11.5, fontFace: F.body, color: 'CADCFC',
    align: 'left', valign: 'top', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 10 — Property-based search deep dive
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'GATE 4 · DEEP DIVE', 'Property-based search — the gate that hunts what tests don\'t');

  s.addText('In plain language:', {
    x: 0.5, y: 1.85, w: 6, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'left', margin: 0,
  });
  s.addText(
    'A unit test asks: "for this specific input, does the answer match?". A property test asks: "for ANY input the generator can produce, do both runtimes agree?". ' +
    'If even one input breaks the property, the gate fails — and the failing input is logged for the engineer.',
    {
      x: 0.5, y: 2.25, w: 6, h: 1.8,
      fontSize: 13, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });

  s.addText('Why this matters:', {
    x: 0.5, y: 4.25, w: 6, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'left', margin: 0,
  });
  s.addText([
    { text: 'Catches off-by-one and boundary bugs the test pack assumed away', options: { bullet: true, breakLine: true } },
    { text: 'Generators come from spec generator-hints — typed, schema-checked', options: { bullet: true, breakLine: true } },
    { text: 'Shadow mode (Phase 9.3) → live mode v1.1 (real ref/candidate binaries)', options: { bullet: true } },
  ], {
    x: 0.5, y: 4.65, w: 6, h: 2.1,
    fontSize: 12, fontFace: F.body, color: C.text,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 5,
  });

  // Right: flow diagram for one gate run
  const dx = 7.0;
  s.addText('Inside a single gate run', {
    x: dx, y: 1.85, w: 5.8, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.text, bold: true,
    align: 'left', margin: 0,
  });

  const flow = [
    'Generate 100 random inputs (from spec hints)',
    'Run each input through the reference (Delphi/C++)',
    'Run each input through the .NET 8 candidate',
    'Compare outputs — any disagreement = falsifier',
    'Report passed / failed + the smallest failing input',
  ];
  const sy = 2.35;
  flow.forEach((step, i) => {
    const y = sy + i * 0.85;
    s.addShape(pres.shapes.OVAL, {
      x: dx, y: y + 0.05, w: 0.45, h: 0.45,
      fill: { color: C.primary }, line: { type: 'none' },
    });
    s.addText(String(i + 1), {
      x: dx, y: y + 0.05, w: 0.45, h: 0.45,
      fontSize: 14, fontFace: F.head, color: C.white, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x: dx + 0.6, y, w: 5.3, h: 0.55,
      fill: { color: C.white }, line: { color: C.border, width: 1 },
    });
    s.addText(step, {
      x: dx + 0.75, y, w: 5.0, h: 0.55,
      fontSize: 12, fontFace: F.body, color: C.text,
      align: 'left', valign: 'middle', margin: 0,
    });
    if (i < flow.length - 1) {
      arrowDown(s, dx + 0.225, y + 0.55, 0.25, C.primary);
    }
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 11 — Migration planning algorithm
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'PLANNING', 'Migration plan — Kahn\'s algorithm on the SCC condensation');

  s.addText(
    'For a 735-routine corpus, "migrate everything" isn\'t a plan. We turn the dependency graph into discrete, ' +
    'ordered waves so each wave depends only on the ones before it.',
    {
      x: 0.5, y: 1.85, w: 12.3, h: 0.9,
      fontSize: 13.5, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });

  // Algorithm flow: graph → SCCs → DAG → waves
  const steps = [
    { label: 'Dependency\ngraph',     sub: '735 nodes,\n1.4M edges' },
    { label: 'Tarjan SCC\ncondense',  sub: 'cycles collapsed\nto super-nodes' },
    { label: 'DAG\n(super-graph)',    sub: 'guaranteed\nacyclic' },
    { label: 'Kahn BFS\non DAG',      sub: 'leaves first,\nbreadth-by-breadth' },
    { label: 'Migration\nwaves',      sub: 'wave 1 → wave N\nin topological order' },
  ];

  const bw = 2.1, bh = 1.5, gap = 0.18;
  const totalW = bw * steps.length + gap * (steps.length - 1);
  const sx = (13.3 - totalW) / 2;
  const yRow = 3.0;

  steps.forEach((st, i) => {
    const x = sx + i * (bw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: yRow, w: bw, h: bh,
      fill: { color: C.white }, line: { color: C.primary, width: 1.5 },
      shadow: { type: 'outer', color: '000000', blur: 8, offset: 2, angle: 90, opacity: 0.10 },
    });
    s.addText(st.label, {
      x: x + 0.1, y: yRow + 0.2, w: bw - 0.2, h: 0.7,
      fontSize: 14, fontFace: F.head, color: C.text, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(st.sub, {
      x: x + 0.1, y: yRow + 0.95, w: bw - 0.2, h: 0.5,
      fontSize: 10, fontFace: F.body, color: C.muted, italic: true,
      align: 'center', valign: 'top', margin: 0,
    });
    if (i < steps.length - 1) {
      arrowRight(s, x + bw + 0.01, yRow + bh / 2, gap - 0.02, C.primary);
    }
  });

  // Strategy pills below
  s.addText('Four strategies — pick the one that fits the engagement:', {
    x: 0.5, y: 4.8, w: 12.3, h: 0.4,
    fontSize: 13, fontFace: F.head, color: C.text, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });

  const strategies = [
    { name: 'topological-leaves-first', desc: 'Default. Wave 1 = leaves; safest.' },
    { name: 'business-priority',        desc: 'Caller-supplied per-routine priorities.' },
    { name: 'pilot-then-scale',         desc: 'Wave 1 = chosen pilots; then topology.' },
    { name: 'risk-first',               desc: 'Highest blast-radius routines first within each wave.' },
  ];
  const pw = 2.95, ph = 1.25, pgap = 0.15;
  strategies.forEach((st, i) => {
    const x = 0.5 + i * (pw + pgap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 5.4, w: pw, h: ph,
      fill: { color: 'EEF2FF' }, line: { color: C.primaryDk, width: 1 },
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 5.4, w: 0.1, h: ph,
      fill: { color: C.primaryDk }, line: { type: 'none' },
    });
    s.addText(st.name, {
      x: x + 0.25, y: 5.5, w: pw - 0.35, h: 0.45,
      fontSize: 12.5, fontFace: F.head, color: C.primaryDk, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(st.desc, {
      x: x + 0.25, y: 5.9, w: pw - 0.35, h: 0.65,
      fontSize: 11, fontFace: F.body, color: C.text,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  s.addText('Indy Sockets: 4 waves over 735 routines.    fmt: 3 waves over 769 routines.', {
    x: 0.5, y: 6.85, w: 12.3, h: 0.3,
    fontSize: 11, fontFace: F.body, color: C.muted, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 12 — Delphi-specific design
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'DELPHI', 'What had to change for Delphi specifically');

  // Three column layout: Parser / Schema / Archetype
  const cols = [
    {
      heading: 'Parser',
      sub: 'tree-sitter-pascal',
      points: [
        'In-process grammar — no fpc shell-out',
        'Resolves uses-clause across .pas + .dpr',
        'Recognises TComponent owner chain',
        'Indexes ARC interface declarations',
        'Captures RTTI hook sites',
      ],
      color: C.delphi,
    },
    {
      heading: 'Schema',
      sub: '5 new claim kinds',
      points: [
        'object_lifetime — owner, ARC, destructor order',
        'interface_implementation — IInterface surfaces',
        'property_accessor — read/write/default semantics',
        'event_handler_contract — when fires, what args',
        'rtti_usage — runtime type queries',
      ],
      color: C.delphi,
    },
    {
      heading: 'Archetype',
      sub: 'canonical-delphi-idsmtp',
      points: [
        '6 files — csproj + xUnit + 3 source files',
        'IDisposable swaps for ARC interfaces',
        'TComponent → C# class implementing host interface',
        'TStrings → IList<string>; TStream → Stream',
        'Tested on IndySockets TIdSMTP.Connect',
      ],
      color: C.delphi,
    },
  ];

  const cw = 4.05, ch = 5.0, gap = 0.18;
  const totalW = cw * 3 + gap * 2;
  const sx = (13.3 - totalW) / 2;
  cols.forEach((c, i) => {
    const x = sx + i * (cw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: ch,
      fill: { color: C.white }, line: { color: c.color, width: 1.5 },
      shadow: { type: 'outer', color: '000000', blur: 10, offset: 3, angle: 90, opacity: 0.08 },
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: 0.45,
      fill: { color: c.color }, line: { type: 'none' },
    });
    s.addText(c.heading, {
      x: x + 0.2, y: 1.85, w: cw - 0.4, h: 0.45,
      fontSize: 16, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(c.sub, {
      x: x + 0.2, y: 2.45, w: cw - 0.4, h: 0.4,
      fontSize: 11.5, fontFace: F.head, color: c.color, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(
      c.points.map((p, k) => ({ text: p, options: { bullet: true, breakLine: k < c.points.length - 1 } })),
      {
        x: x + 0.2, y: 2.95, w: cw - 0.35, h: ch - 1.2,
        fontSize: 11.5, fontFace: F.body, color: C.text,
        align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 6,
      });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 13 — C++-specific design
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'C++', 'What had to change for C++ specifically');

  const cols = [
    {
      heading: 'Parser',
      sub: 'libclang AST',
      points: [
        'Runs the real Clang frontend',
        'Handles templates, lambdas, C++20 features',
        'Honours preprocessor branches',
        'Captures __attribute__((noreturn))',
        'Resolves typedefs + using declarations',
      ],
      color: C.cpp,
    },
    {
      heading: 'Schema',
      sub: '3 new claim kinds',
      points: [
        'template_instantiation — per-T contract surface',
        'undefined_behavior — UB risks (signed overflow, etc.)',
        'exception_contract — noexcept / throws what',
        '6 kinds shared with Delphi',
        'generator-hints feed Gate 4',
      ],
      color: C.cpp,
    },
    {
      heading: 'Archetype',
      sub: 'canonical-cpp-fmt',
      points: [
        '6 files — csproj + xUnit + 3 source files',
        'template<...> lowered to params object?[]',
        'Strongly-typed Format<T> for int/float/string',
        '[SpecClaim] attributes on every public surface',
        'Tested on fmtlib fmt::format',
      ],
      color: C.cpp,
    },
  ];

  const cw = 4.05, ch = 5.0, gap = 0.18;
  const totalW = cw * 3 + gap * 2;
  const sx = (13.3 - totalW) / 2;
  cols.forEach((c, i) => {
    const x = sx + i * (cw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: ch,
      fill: { color: C.white }, line: { color: c.color, width: 1.5 },
      shadow: { type: 'outer', color: '000000', blur: 10, offset: 3, angle: 90, opacity: 0.08 },
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: 0.45,
      fill: { color: c.color }, line: { type: 'none' },
    });
    s.addText(c.heading, {
      x: x + 0.2, y: 1.85, w: cw - 0.4, h: 0.45,
      fontSize: 16, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(c.sub, {
      x: x + 0.2, y: 2.45, w: cw - 0.4, h: 0.4,
      fontSize: 11.5, fontFace: F.head, color: '92400E', bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(
      c.points.map((p, k) => ({ text: p, options: { bullet: true, breakLine: k < c.points.length - 1 } })),
      {
        x: x + 0.2, y: 2.95, w: cw - 0.35, h: ch - 1.2,
        fontSize: 11.5, fontFace: F.body, color: C.text,
        align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 6,
      });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 14 — Sidecar architecture
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'ARCHITECTURE', 'Sidecar components — each language has its own toolchain');

  s.addText('The API never compiles or runs untrusted source itself. Each language\'s toolchain lives in a sidecar container — clean isolation, easy to scale.', {
    x: 0.5, y: 1.85, w: 12.3, h: 0.55,
    fontSize: 13, fontFace: F.body, color: C.muted,
    align: 'left', valign: 'top', margin: 0,
  });

  // ── Bus-style architecture: API bar across the middle, sidecars
  //    arranged above (parse/build) and below (run/store). Each sidecar
  //    has a short vertical connector — no crossing lines.
  const apiBarY = 4.35, apiBarH = 0.55;
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0.5, y: apiBarY, w: 12.3, h: apiBarH,
    fill: { color: C.primaryDk }, line: { type: 'none' },
    shadow: { type: 'outer', color: '000000', blur: 10, offset: 3, angle: 90, opacity: 0.18 },
  });
  s.addText('Astra API  ·  .NET 8  ·  Auth · Audit · Orchestration', {
    x: 0.5, y: apiBarY, w: 12.3, h: apiBarH,
    fontSize: 15, fontFace: F.head, color: C.white, bold: true,
    align: 'center', valign: 'middle', margin: 0, charSpacing: 2,
  });

  // Lane labels
  s.addText('PARSE  ·  COMPILE', {
    x: 0.5, y: 2.55, w: 12.3, h: 0.25,
    fontSize: 10, fontFace: F.head, color: C.muted, bold: true,
    align: 'center', valign: 'middle', margin: 0, charSpacing: 3,
  });
  s.addText('RUN  ·  PROPERTY  ·  STORE', {
    x: 0.5, y: 4.97, w: 12.3, h: 0.25,
    fontSize: 10, fontFace: F.head, color: C.muted, bold: true,
    align: 'center', valign: 'middle', margin: 0, charSpacing: 3,
  });

  // Top tier — parse + compile
  const above = [
    { label: 'parser-sidecar',   sub: 'tree-sitter-pascal\nlibclang · fparser2',  color: C.primary },
    { label: 'fpc-sidecar',      sub: 'Delphi compile',  color: C.delphi  },
    { label: 'gpp-sidecar',      sub: 'C++ compile',  color: C.cpp     },
    { label: 'gfortran-sidecar', sub: 'Fortran compile',  color: C.fortran },
    { label: 'gnucobol-sidecar', sub: 'COBOL compile',  color: C.cobol   },
    { label: 'maven-sidecar',    sub: 'Java target build',  color: '64748B'  },
  ];
  // Bottom tier — runtime + storage
  const below = [
    { label: 'fpc-runtime',         sub: 'Delphi binary exec',  color: C.delphi },
    { label: 'gpp-runtime',         sub: 'C++ binary exec',     color: C.cpp    },
    { label: 'property-test-sidecar',sub: 'Gate 4 hypothesis',  color: C.warn   },
    { label: 'gfortran-runtime',    sub: 'Fortran binary exec', color: C.fortran},
    { label: 'minio',               sub: 'artifact store',      color: '64748B' },
    { label: 'postgres',            sub: 'audit log + state',   color: '64748B' },
  ];

  const tileW = 1.95, tileH = 0.95, tileGap = 0.10;
  const totalW = tileW * 6 + tileGap * 5;
  const startX = (13.3 - totalW) / 2;

  const drawTile = (sc, x, y) => {
    s.addShape(pres.shapes.RECTANGLE, {
      x, y, w: tileW, h: tileH,
      fill: { color: C.white }, line: { color: sc.color, width: 1.5 },
      shadow: { type: 'outer', color: '000000', blur: 6, offset: 2, angle: 90, opacity: 0.08 },
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x, y, w: 0.08, h: tileH,
      fill: { color: sc.color }, line: { type: 'none' },
    });
    s.addText(sc.label, {
      x: x + 0.16, y: y + 0.06, w: tileW - 0.22, h: 0.34,
      fontSize: 10.5, fontFace: F.head, color: C.text, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(sc.sub, {
      x: x + 0.16, y: y + 0.40, w: tileW - 0.22, h: tileH - 0.42,
      fontSize: 9, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });
  };

  const topY = 2.85, botY = 5.27;
  above.forEach((sc, i) => {
    const x = startX + i * (tileW + tileGap);
    drawTile(sc, x, topY);
    s.addShape(pres.shapes.LINE, {
      x: x + tileW / 2, y: topY + tileH, w: 0, h: apiBarY - (topY + tileH),
      line: { color: C.border, width: 1, dashType: 'dash' },
    });
  });
  below.forEach((sc, i) => {
    const x = startX + i * (tileW + tileGap);
    drawTile(sc, x, botY);
    s.addShape(pres.shapes.LINE, {
      x: x + tileW / 2, y: apiBarY + apiBarH, w: 0, h: botY - (apiBarY + apiBarH),
      line: { color: C.border, width: 1, dashType: 'dash' },
    });
  });

  s.addText('Bring up the entire stack with one command:  docker compose up -d', {
    x: 0.5, y: 6.4, w: 12.3, h: 0.4,
    fontSize: 11, fontFace: F.body, color: C.muted, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 15 — Nous added value scorecard
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  pageHeader(s, 'SCORECARD', 'Raw LLM vs the Nous harness');

  // Header row — table starts at y=1.7, rowH tightened so 8 rows fit cleanly
  // above the footer (~y=7.0). 1.7 + 0.5 (header) + 8 × 0.50 = 6.2 < 7.0.
  const tx = 0.5, tw = 12.3, headerH = 0.5, rowH = 0.55;
  const colW = [4.5, 3.5, 4.3];
  const xCols = [tx, tx + colW[0], tx + colW[0] + colW[1]];

  // Header
  ['Capability', 'Raw LLM call', 'Nous harness'].forEach((h, i) => {
    s.addShape(pres.shapes.RECTANGLE, {
      x: xCols[i], y: 1.7, w: colW[i], h: headerH,
      fill: { color: i === 2 ? C.primaryDk : (i === 1 ? '94A3B8' : '1E293B') },
      line: { type: 'none' },
    });
    s.addText(h, {
      x: xCols[i] + 0.2, y: 1.7, w: colW[i] - 0.4, h: headerH,
      fontSize: 13, fontFace: F.head, color: C.white, bold: true,
      align: i === 0 ? 'left' : 'center', valign: 'middle', margin: 0,
    });
  });

  const rows = [
    ['Parses Delphi / C++ with real grammar',                  '— guesses from text',         '✓ tree-sitter-pascal + libclang'],
    ['Routes source-line citations into every claim',          '— may hallucinate line refs', '✓ schema-enforced, validated server-side'],
    ['SME review with claim-level evidence',                   '— no review surface',         '✓ Accept / Edit / Resolve per claim'],
    ['Immutable audit log of every decision',                  '— no audit',                  '✓ event sourcing + Postgres'],
    ['Buildable .NET 8 scaffold from a signed spec',           '— free-form code, may fail',  '✓ archetype + LLM = always compiles'],
    ['Cross-runtime equivalence + property-based search',      '— no validation',             '✓ 4 independent gates'],
    ['Wave-by-wave migration plan for the whole portfolio',    '— per-routine only',          '✓ Kahn on SCC condensation'],
    ['Add a new source language without rewriting the core',   '— prompt redesign',           '✓ schema + archetype pluggable'],
  ];

  rows.forEach((r, i) => {
    const y = 1.7 + headerH + i * rowH;
    // Alternating row tint
    if (i % 2 === 0) {
      s.addShape(pres.shapes.RECTANGLE, {
        x: tx, y, w: tw, h: rowH,
        fill: { color: 'F1F5F9' }, line: { type: 'none' },
      });
    }
    s.addText(r[0], {
      x: xCols[0] + 0.2, y, w: colW[0] - 0.4, h: rowH,
      fontSize: 11.5, fontFace: F.body, color: C.text,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(r[1], {
      x: xCols[1] + 0.1, y, w: colW[1] - 0.2, h: rowH,
      fontSize: 11, fontFace: F.body, color: C.muted, italic: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(r[2], {
      x: xCols[2] + 0.2, y, w: colW[2] - 0.4, h: rowH,
      fontSize: 11.5, fontFace: F.body, color: C.ok, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 16 — Close / next steps
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  s.background = { color: C.bgDark };
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0, y: 0, w: 0.18, h: 7.5,
    fill: { color: C.primary }, line: { type: 'none' },
  });

  s.addText('Where we go next', {
    x: 0.7, y: 1.2, w: 12, h: 1.0,
    fontSize: 48, fontFace: F.head, color: C.white, bold: true,
    align: 'left', margin: 0,
  });
  s.addText('Delphi & C++ are live. The next two milestones extend the same pattern.', {
    x: 0.7, y: 2.3, w: 12, h: 0.6,
    fontSize: 18, fontFace: F.head, color: 'CADCFC', italic: true,
    align: 'left', margin: 0,
  });

  const cards = [
    {
      tag: 'IMMEDIATE',
      title: 'Live mode for Gate 4',
      body: 'Wire the real reference + candidate binaries into the property-based search so falsifying inputs are reproducible end-to-end.',
      color: C.ok,
    },
    {
      tag: 'NEXT',
      title: 'PL/SQL + RPG migration',
      body: 'Same pattern: parser sidecar + schema kinds + archetype. The harness was designed to add languages without touching the core.',
      color: C.warn,
    },
    {
      tag: 'AFTER',
      title: 'Customer-owned archetypes',
      body: 'Engagement teams ship corpus-specific archetypes alongside the canonical ones — the matches.anyOf field already supports this.',
      color: C.delphi,
    },
  ];

  const cw = 4.0, ch = 2.7, gap = 0.2;
  const totalW = cw * 3 + gap * 2;
  const sx = (13.3 - totalW) / 2;
  cards.forEach((c, i) => {
    const x = sx + i * (cw + gap);
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 3.3, w: cw, h: ch,
      fill: { color: '283466' }, line: { color: c.color, width: 1.5 },
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x: x + 0.25, y: 3.5, w: 1.5, h: 0.32,
      fill: { color: c.color }, line: { type: 'none' },
    });
    s.addText(c.tag, {
      x: x + 0.25, y: 3.5, w: 1.5, h: 0.32,
      fontSize: 10, fontFace: F.head, color: C.white, bold: true,
      align: 'center', valign: 'middle', margin: 0, charSpacing: 2,
    });
    s.addText(c.title, {
      x: x + 0.25, y: 3.95, w: cw - 0.5, h: 0.5,
      fontSize: 18, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(c.body, {
      x: x + 0.25, y: 4.55, w: cw - 0.5, h: 1.3,
      fontSize: 12, fontFace: F.body, color: 'CADCFC',
      align: 'left', valign: 'top', margin: 0,
    });
  });

  s.addText('Astra RE Harness  ·  Nous', {
    x: 0.7, y: 6.7, w: 12, h: 0.4,
    fontSize: 12, fontFace: F.head, color: C.primary, bold: true,
    align: 'left', margin: 0, charSpacing: 6,
  });
}

// ── Write ─────────────────────────────────────────────────────────────────
const OUTPATH = 'C:/Astra RE Harness/app/e2e/demo-output/phase-9.6-delphi-cpp-technical-solution.pptx';
pres.writeFile({ fileName: OUTPATH }).then((p) => {
  console.log(`Wrote: ${p}`);
});
