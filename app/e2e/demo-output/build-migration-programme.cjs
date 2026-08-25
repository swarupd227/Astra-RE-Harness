/**
 * Delphi & C++ Modernisation Programme — Customer-facing deck.
 *
 * Reframes the Phase 9.6 technical deck as a migration PROGRAMME pitch:
 *   • theme matches the Nous "AI-Native" sample (dark navy + cyan + amber)
 *   • COBOL / Fortran dropped — only Delphi & C++ in the headline narrative
 *   • technical jargon translated to plain English (archetype → blueprint, etc.)
 *   • spec-driven + AI co-pilot positioning made explicit
 *   • storyline = 5-phase migration programme, accelerated by Astra (not a platform pitch)
 */
const pptxgen = require('pptxgenjs');

const pres = new pptxgen();
pres.layout = 'LAYOUT_WIDE';
pres.title  = 'Delphi & C++ Modernisation Programme — Nous · Astra';
pres.author = 'Nous Infosystems';

// ── Palette — matched to the Nous template ───────────────────────────────
const C = {
  bgDark:    '012640',  // very deep navy (page bg)
  bgPanel:   '0A2E48',  // slightly lighter band — section bars
  bgCard:    '0E3A57',  // card surface
  cardLine:  '1A4F74',  // card border
  cyan:      '33B5E5',  // primary accent (titles / numerics)
  cyanDeep:  '0E8FBF',  // pressed-state accent
  amber:     'F5A623',  // secondary accent (highlights, separators)
  white:     'FFFFFF',
  muted:     'B0BAC8',  // body sub-text
  faint:     '8FA3B5',
  green:     '2BA66B',  // positive
  red:       'D94E4E',  // negative
};

const F = {
  head: 'Calibri',     // bold weight for titles
  body: 'Calibri',
};

// Slide dimensions — LAYOUT_WIDE is 13.333" × 7.5"
const SW = 13.33, SH = 7.5;

// ── Helpers ──────────────────────────────────────────────────────────────
function chrome(slide, title, subtitle) {
  slide.background = { color: C.bgDark };

  // Title in cyan, top-left
  slide.addText(title, {
    x: 0.5, y: 0.35, w: SW - 1.5, h: 0.55,
    fontSize: 26, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });

  // Optional subtitle/panel below title
  if (subtitle) {
    slide.addShape(pres.shapes.RECTANGLE, {
      x: 0.5, y: 1.0, w: SW - 1.0, h: 0.45,
      fill: { color: C.bgPanel }, line: { type: 'none' },
    });
    slide.addText(subtitle, {
      x: 0.7, y: 1.0, w: SW - 1.4, h: 0.45,
      fontSize: 13, fontFace: F.body, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
  }

  // NOUS branding top-right — single horizontal block (NOUS bold + INFOSYSTEMS spaced)
  slide.addText([
    { text: 'NOUS ', options: { fontSize: 14, bold: true } },
    { text: 'INFOSYSTEMS', options: { fontSize: 9, charSpacing: 2 } },
  ], {
    x: SW - 3.0, y: 0.35, w: 2.6, h: 0.32,
    fontFace: F.head, color: C.white,
    align: 'right', valign: 'middle', margin: 0,
  });

  // Footer
  slide.addText('Nous Infosystems  ·  Delphi & C++ Modernisation Programme  ·  Confidential', {
    x: 0.5, y: SH - 0.35, w: SW - 1.0, h: 0.25,
    fontSize: 8, fontFace: F.body, color: C.faint,
    align: 'left', valign: 'middle', margin: 0,
  });
}

function card(slide, x, y, w, h, fill) {
  slide.addShape(pres.shapes.RECTANGLE, {
    x, y, w, h,
    fill: { color: fill || C.bgCard },
    line: { color: C.cardLine, width: 1 },
  });
}

function arrowRight(slide, x, y, w) {
  slide.addShape(pres.shapes.LINE, {
    x, y, w, h: 0,
    line: { color: C.cyan, width: 2.5, endArrowType: 'triangle' },
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 1 — Title
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  s.background = { color: C.bgDark };

  // Top-right NOUS brand — single horizontal block
  s.addText([
    { text: 'NOUS ', options: { fontSize: 18, bold: true } },
    { text: 'INFOSYSTEMS', options: { fontSize: 10, charSpacing: 2 } },
  ], {
    x: SW - 3.5, y: 0.35, w: 3.1, h: 0.4,
    fontFace: F.head, color: C.white,
    align: 'right', valign: 'middle', margin: 0,
  });

  // Headline
  s.addText('Modernising Legacy', {
    x: 0.7, y: 2.0, w: SW - 1.4, h: 0.9,
    fontSize: 44, fontFace: F.head, color: C.white, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText('Delphi & C++ to .NET 10', {
    x: 0.7, y: 2.85, w: SW - 1.4, h: 1.0,
    fontSize: 52, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });

  // Subtitle row — methodology pillars
  s.addText('Spec-Driven Migration  ·  AI Co-Pilots  ·  4-Gate Validation', {
    x: 0.7, y: 4.0, w: SW - 1.4, h: 0.5,
    fontSize: 20, fontFace: F.head, color: C.cyan, italic: true,
    align: 'left', valign: 'middle', margin: 0,
  });

  // Amber accent line
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0.7, y: 4.55, w: 3.0, h: 0.06,
    fill: { color: C.amber }, line: { type: 'none' },
  });

  // Three KPI tiles
  const tiles = [
    { num: '2-3×',  label: 'Faster than manual rewrite' },
    { num: '100%',  label: 'Audit-traceable behaviour rules' },
    { num: '4 of 4', label: 'Quality gates before cutover' },
  ];
  const tw = 3.5, th = 1.4, gap = 0.3;
  const totalW = tw * 3 + gap * 2;
  const startX = 0.7;
  tiles.forEach((t, i) => {
    const x = startX + i * (tw + gap);
    card(s, x, 5.0, tw, th);
    s.addText(t.num, {
      x: x + 0.2, y: 5.1, w: tw - 0.4, h: 0.65,
      fontSize: 30, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(t.label, {
      x: x + 0.2, y: 5.75, w: tw - 0.4, h: 0.5,
      fontSize: 11.5, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'middle', margin: 0,
    });
  });

  s.addText('A Nous Infosystems delivery proposal', {
    x: 0.7, y: 6.7, w: SW - 1.4, h: 0.3,
    fontSize: 11, fontFace: F.body, color: C.faint, italic: true,
    align: 'left', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 2 — Why modernise now
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'Why modernise Delphi & C++ now',
    'Five business drivers shaping every legacy modernisation conversation in 2026.');

  const drivers = [
    { num: '01', t: 'Vanishing talent',
      d: 'Delphi & C++ developers retiring faster than they\'re hired. Day-to-day support costs are rising and operational risk is climbing.' },
    { num: '02', t: 'Cloud-native target',
      d: '.NET 10 unlocks container-first deployment, modern observability, managed identity, and the rest of your existing cloud platform.' },
    { num: '03', t: 'Security & compliance',
      d: 'Modern dependency scanning, signed artefacts, supply-chain attestation — all standard in the .NET 10 ecosystem.' },
    { num: '04', t: 'Test & velocity',
      d: 'A test pack the legacy code never had. CI/CD becomes possible. Release cadence moves from quarters to weeks.' },
    { num: '05', t: 'AI leverage',
      d: 'Once on a modern stack, AI co-pilots accelerate every subsequent change — the modernisation pays for itself.' },
  ];

  const cw = 2.42, ch = 4.5, gap = 0.15;
  const totalW = cw * 5 + gap * 4;
  const startX = (SW - totalW) / 2;
  drivers.forEach((d, i) => {
    const x = startX + i * (cw + gap);
    card(s, x, 1.8, cw, ch);
    // Big number
    s.addText(d.num, {
      x: x + 0.2, y: 1.95, w: cw - 0.4, h: 0.6,
      fontSize: 28, fontFace: F.head, color: C.cyan, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    // Amber underline accent below the number
    s.addShape(pres.shapes.RECTANGLE, {
      x: x + 0.2, y: 2.55, w: 0.6, h: 0.04,
      fill: { color: C.amber }, line: { type: 'none' },
    });
    s.addText(d.t, {
      x: x + 0.2, y: 2.75, w: cw - 0.4, h: 0.6,
      fontSize: 14, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'top', margin: 0,
    });
    s.addText(d.d, {
      x: x + 0.2, y: 3.4, w: cw - 0.4, h: ch - 1.6,
      fontSize: 10.5, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  s.addText('The question isn\'t whether to migrate — it\'s how to migrate without re-introducing the bugs you spent twenty years finding.', {
    x: 0.5, y: 6.55, w: SW - 1.0, h: 0.4,
    fontSize: 13, fontFace: F.body, color: C.cyan, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 3 — The migration challenge (manual baseline)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'The migration challenge — without acceleration',
    'A manual Delphi/C++ rewrite hits five predictable walls. Every programme we\'ve recovered started here.');

  const walls = [
    { t: 'Knowledge is in the code, not the docs',
      d: 'Twenty years of business rules live as side-effects, edge-case branches, and "don\'t touch this" comments. Reverse-engineering takes longer than the original build.' },
    { t: 'Behaviour parity is hard to prove',
      d: 'Re-writes "look right" but quietly drift. Differences surface in production months later — exactly when the original team has rolled off.' },
    { t: 'Migration order matters — and is non-obvious',
      d: 'Pick the wrong routine first and you block yourself for weeks. There\'s no clean way to see the call graph across 700+ routines.' },
    { t: 'SME bandwidth becomes the bottleneck',
      d: 'Only one or two people understand each subsystem. They\'re drowning in review work and the programme stalls waiting for sign-off.' },
    { t: 'No audit trail when something breaks',
      d: '"Why was this decision made?" can\'t be answered three months later. Compliance and risk teams won\'t sign off.' },
  ];

  const cw = 12.3, ch = 0.9, gap = 0.12;
  walls.forEach((w, i) => {
    const y = 1.8 + i * (ch + gap);
    card(s, 0.5, y, cw, ch);
    // Red X badge
    s.addShape(pres.shapes.RECTANGLE, {
      x: 0.5, y, w: 0.45, h: ch,
      fill: { color: C.red }, line: { type: 'none' },
    });
    s.addText('✗', {
      x: 0.5, y, w: 0.45, h: ch,
      fontSize: 18, fontFace: F.head, color: C.white, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(w.t, {
      x: 1.05, y: y + 0.08, w: 4.5, h: 0.4,
      fontSize: 13, fontFace: F.head, color: C.cyan, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(w.d, {
      x: 5.6, y: y + 0.05, w: cw - 5.3, h: ch - 0.1,
      fontSize: 11, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'middle', margin: 0,
    });
  });

  s.addText('Each wall is a programme delay measured in months — and a quiet quality risk that surfaces post-cutover.', {
    x: 0.5, y: 6.5, w: SW - 1.0, h: 0.4,
    fontSize: 12, fontFace: F.body, color: C.white, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 4 — Our approach (4 principles)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'Our approach — Spec-Driven, AI-Native Migration',
    'Four principles guide every routine through the programme. They turn each of the five walls above into a solved problem.');

  const principles = [
    { num: '01', t: 'Spec-driven, not code-first',
      d: 'For every routine we write a machine-readable contract — its inputs, outputs, invariants, and edge cases — BEFORE writing the .NET 10 code. The contract is the source of truth.' },
    { num: '02', t: 'AI co-pilots, human gates',
      d: 'Claude drafts contracts and writes the .NET 10 starter code. People — engineer and SME — review and sign off. Speed without giving up control.' },
    { num: '03', t: 'Evidence on every claim',
      d: 'Every line of the contract cites the exact source lines it came from. Reviewers verify against the original Delphi/C++ on the same screen. No unchecked hallucination.' },
    { num: '04', t: 'Wave-by-wave, dependency-aware',
      d: 'We migrate in waves so each wave depends only on what already shipped. The whole portfolio moves together — never half-migrated, half-legacy.' },
  ];

  const cw = 6.0, ch = 2.55, gap = 0.25;
  principles.forEach((p, i) => {
    const col = i % 2, row = Math.floor(i / 2);
    const x = 0.55 + col * (cw + gap);
    const y = 1.85 + row * (ch + gap);
    card(s, x, y, cw, ch);
    s.addText(p.num, {
      x: x + 0.3, y: y + 0.2, w: 1.0, h: 0.5,
      fontSize: 24, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(p.t, {
      x: x + 1.3, y: y + 0.2, w: cw - 1.5, h: 0.5,
      fontSize: 16, fontFace: F.head, color: C.cyan, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(p.d, {
      x: x + 0.3, y: y + 0.85, w: cw - 0.5, h: ch - 1.05,
      fontSize: 12, fontFace: F.body, color: C.white,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  s.addText('These four principles are encoded into our Astra accelerator — they aren\'t a slide-ware promise.', {
    x: 0.5, y: 7.0, w: SW - 1.0, h: 0.3,
    fontSize: 12, fontFace: F.body, color: C.amber, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 5 — 5-phase programme overview
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'The 5-phase migration programme',
    'A measured, evidence-gated rollout — prove it on a pilot, then scale wave by wave.');

  const phases = [
    { num: '01', t: 'Discovery',  when: 'Weeks 0–4',  d: ['Crawl every Delphi/C++ source file', 'Build the routine catalogue + call graph', 'Identify pilot candidates with SMEs', 'Agree on success measures'] },
    { num: '02', t: 'Pilot',      when: 'Weeks 4–10', d: ['Migrate 5–8 routines end-to-end', 'Baseline accelerator throughput', 'Refine contracts + review workflow', 'Quality gate against legacy behaviour'] },
    { num: '03', t: 'Wave-by-Wave', when: 'Months 3–N', d: ['Move 50–100 routines per wave', 'Highest blast-radius first (risk-first)', 'Run all 4 quality gates per routine', 'Parallel SME review across pods'] },
    { num: '04', t: 'Cutover',    when: 'Per wave',    d: ['Production rollout per wave', 'Shadow traffic + traffic-shifted cutover', 'Roll-back drill on every wave', 'Decommission migrated legacy modules'] },
    { num: '05', t: 'Run & KT',   when: 'Months N+1+', d: ['Knowledge transfer to your team', 'Astra remains as institutional memory', 'Continuous test pack + 4-gate runs', '24×7 support during steady-state'] },
  ];

  const cw = 2.45, ch = 4.6, gap = 0.1;
  const totalW = cw * 5 + gap * 4;
  const startX = (SW - totalW) / 2;
  const cardY = 1.85;
  phases.forEach((p, i) => {
    const x = startX + i * (cw + gap);
    card(s, x, cardY, cw, ch);
    s.addText(p.num, {
      x: x + 0.2, y: cardY + 0.18, w: cw - 0.4, h: 0.55,
      fontSize: 30, fontFace: F.head, color: C.cyan, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(p.t, {
      x: x + 0.2, y: cardY + 0.78, w: cw - 0.4, h: 0.45,
      fontSize: 16, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(p.when, {
      x: x + 0.2, y: cardY + 1.2, w: cw - 0.4, h: 0.32,
      fontSize: 10, fontFace: F.body, color: C.amber, italic: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(p.d.map((line, k) => ({
      text: line, options: { bullet: true, breakLine: k < p.d.length - 1 },
    })), {
      x: x + 0.2, y: cardY + 1.55, w: cw - 0.3, h: ch - 1.7,
      fontSize: 10.5, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 4,
    });
    // Subtle phase divider arrow on right edge
    if (i < phases.length - 1) {
      s.addShape(pres.shapes.LINE, {
        x: x + cw + 0.005, y: cardY + ch / 2, w: gap - 0.01, h: 0,
        line: { color: C.amber, width: 1.5, endArrowType: 'triangle' },
      });
    }
  });

  s.addText('Each phase has an explicit exit criterion — no phase moves on quietly.', {
    x: 0.5, y: 6.7, w: SW - 1.0, h: 0.3,
    fontSize: 12, fontFace: F.body, color: C.amber, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 6 — Spec-driven development explained
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'Spec-Driven Development — what it means here',
    'For each routine, the contract is written first, signed by an SME, and becomes the source of truth for the rewrite.');

  // Left: the flow (5 steps as numbered circles down the left)
  const steps = [
    { t: 'Extract',  d: 'Astra reads the Delphi or C++ source and proposes a contract: inputs, outputs, invariants, edge cases — every line cited.' },
    { t: 'Review',   d: 'The engineer scans the contract for obvious misses, then routes it to the SME with one click.' },
    { t: 'Sign',     d: 'SME accepts each behaviour rule, edits where Astra got it wrong, and signs. Every action is logged.' },
    { t: 'Generate', d: 'From the signed contract, Astra emits a buildable .NET 10 starter kit — class, tests, project file.' },
    { t: 'Validate', d: 'Four quality gates run against the legacy behaviour. Only green passes ship.' },
  ];
  const sy = 1.85, sh = 0.95, sgap = 0.12;
  steps.forEach((st, i) => {
    const y = sy + i * (sh + sgap);
    card(s, 0.55, y, 6.5, sh);
    // Circle with number
    s.addShape(pres.shapes.OVAL, {
      x: 0.7, y: y + 0.18, w: 0.6, h: 0.6,
      fill: { color: C.cyan }, line: { type: 'none' },
    });
    s.addText(String(i + 1), {
      x: 0.7, y: y + 0.18, w: 0.6, h: 0.6,
      fontSize: 22, fontFace: F.head, color: C.bgDark, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(st.t, {
      x: 1.5, y: y + 0.1, w: 1.3, h: 0.35,
      fontSize: 14, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(st.d, {
      x: 1.5, y: y + 0.42, w: 5.5, h: sh - 0.48,
      fontSize: 11, fontFace: F.body, color: C.white,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  // Right: "why this matters" panel
  card(s, 7.4, 1.85, 5.4, sy + 4 * (sh + sgap) + sh - 1.85);
  s.addText('Why this approach', {
    x: 7.6, y: 2.0, w: 5.0, h: 0.45,
    fontSize: 18, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addShape(pres.shapes.RECTANGLE, {
    x: 7.6, y: 2.5, w: 0.8, h: 0.05,
    fill: { color: C.amber }, line: { type: 'none' },
  });
  s.addText([
    { text: 'You can audit any line of new code back to a signed contract back to a Delphi/C++ source line.', options: { bullet: true, breakLine: true } },
    { text: 'SMEs spend time deciding, not transcribing. Their hours are the scarcest resource.', options: { bullet: true, breakLine: true } },
    { text: 'AI does the drudgery — drafting and code emission — under human direction at every quality gate.', options: { bullet: true, breakLine: true } },
    { text: 'Compliance and risk teams get the evidence trail they need to sign off the cutover.', options: { bullet: true, breakLine: true } },
    { text: 'Re-runs are cheap — when a rule changes, Astra re-emits the code and the tests update automatically.', options: { bullet: true } },
  ], {
    x: 7.6, y: 2.7, w: 5.0, h: 3.8,
    fontSize: 12, fontFace: F.body, color: C.white,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 8,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 7 — Phase 3 deep dive (the wave engine)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'Phase 3 — Wave-by-Wave Migration',
    'How a single routine flows through the accelerator. Pods process 50–100 routines per wave in parallel.');

  // The 6-step routine flow
  const flow = [
    { num: 'S1', t: 'Ingest',   d: 'Source files\nimported · indexed' },
    { num: 'S2', t: 'Read',     d: 'Routine + neighbours\nresolved' },
    { num: 'S3', t: 'Contract', d: 'AI co-pilot drafts\nbehaviour contract' },
    { num: 'S4', t: 'Sign',     d: 'SME reviews + signs\nevery rule' },
    { num: 'S5', t: 'Build',    d: 'Astra emits .NET 10\nstarter kit' },
    { num: 'S6', t: 'Validate', d: '4 quality gates\nrun green' },
  ];
  const bw = 1.95, bh = 1.6, gap = 0.15;
  const totalW = bw * 6 + gap * 5;
  const startX = (SW - totalW) / 2;
  const yRow = 1.9;
  flow.forEach((f, i) => {
    const x = startX + i * (bw + gap);
    card(s, x, yRow, bw, bh);
    // Cyan numbered cap
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: yRow, w: bw, h: 0.35,
      fill: { color: C.cyan }, line: { type: 'none' },
    });
    s.addText(f.num, {
      x, y: yRow, w: bw, h: 0.35,
      fontSize: 11, fontFace: F.head, color: C.bgDark, bold: true,
      align: 'center', valign: 'middle', margin: 0, charSpacing: 2,
    });
    s.addText(f.t, {
      x: x + 0.1, y: yRow + 0.4, w: bw - 0.2, h: 0.4,
      fontSize: 15, fontFace: F.head, color: C.white, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(f.d, {
      x: x + 0.1, y: yRow + 0.85, w: bw - 0.2, h: bh - 0.95,
      fontSize: 10, fontFace: F.body, color: C.muted,
      align: 'center', valign: 'top', margin: 0,
    });
    if (i < flow.length - 1) {
      arrowRight(s, x + bw + 0.01, yRow + bh / 2, gap - 0.02);
    }
  });

  // Wave order panel
  card(s, 0.55, 4.0, 6.0, 2.7);
  s.addText('How we order the waves', {
    x: 0.75, y: 4.1, w: 5.6, h: 0.4,
    fontSize: 16, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText('Routines have dependencies — touch one, and you may break ten. We build a call graph and use dependency-aware planning so each wave migrates only what\'s ready.', {
    x: 0.75, y: 4.5, w: 5.6, h: 0.9,
    fontSize: 11.5, fontFace: F.body, color: C.muted,
    align: 'left', valign: 'top', margin: 0,
  });
  s.addText([
    { text: 'Wave 1 = leaves (no in-corpus dependencies) — safest start', options: { bullet: true, breakLine: true } },
    { text: 'Risk-first ordering within each wave: highest blast-radius first', options: { bullet: true, breakLine: true } },
    { text: 'Strategy is configurable — by criticality, by team, by pilot first', options: { bullet: true } },
  ], {
    x: 0.75, y: 5.4, w: 5.6, h: 1.2,
    fontSize: 11, fontFace: F.body, color: C.white,
    align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 4,
  });

  // Parallelism panel
  card(s, 6.75, 4.0, 6.0, 2.7);
  s.addText('Throughput at the pod level', {
    x: 6.95, y: 4.1, w: 5.6, h: 0.4,
    fontSize: 16, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText('A single 4-person pod processes routines in parallel. Astra handles the LLM-bound work (contract drafts + code generation); humans do the judgment work (SME sign-off, edge-case calls).', {
    x: 6.95, y: 4.5, w: 5.6, h: 1.0,
    fontSize: 11.5, fontFace: F.body, color: C.muted,
    align: 'left', valign: 'top', margin: 0,
  });
  // Stat row
  const stats = [
    { n: '50-100', l: 'routines / wave' },
    { n: '2-4 wks', l: 'per wave' },
    { n: '4 pods',  l: 'pilot → full scale' },
  ];
  stats.forEach((st, i) => {
    const x = 6.95 + i * 1.95;
    s.addText(st.n, {
      x, y: 5.65, w: 1.8, h: 0.5,
      fontSize: 22, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(st.l, {
      x, y: 6.15, w: 1.8, h: 0.4,
      fontSize: 10, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'middle', margin: 0,
    });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 8 — 4-gate validation
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'Phase 4 — Validation before cutover',
    'Every routine must pass four independent quality gates. All four must be green before production rollout.');

  const gates = [
    { n: 'GATE 1', t: 'Compiles cleanly',
      d: 'The generated .NET 10 starter kit builds with the standard .NET SDK. Catches type errors, missing imports, broken references.',
      tag: 'in plain English: the new code is syntactically valid',
    },
    { n: 'GATE 2', t: 'Test pack passes',
      d: 'Every behaviour rule in the contract becomes a unit test. xUnit runs the pack. Catches obvious behaviour misses.',
      tag: 'in plain English: it does what we said it does',
    },
    { n: 'GATE 3', t: 'Behaviour parity',
      d: 'Identical inputs run through both the legacy Delphi/C++ AND the new .NET 10 code. Outputs must match. Catches silent drift.',
      tag: 'in plain English: same inputs give same answers',
    },
    { n: 'GATE 4', t: 'Adversarial search',
      d: 'A property-based tester invents 100 random inputs aimed at the contract\'s edge cases. Any input that disagrees fails the gate.',
      tag: 'in plain English: did we miss any sneaky inputs',
    },
  ];

  const cw = 3.0, ch = 4.6, gap = 0.18;
  const totalW = cw * 4 + gap * 3;
  const startX = (SW - totalW) / 2;
  gates.forEach((g, i) => {
    const x = startX + i * (cw + gap);
    card(s, x, 1.85, cw, ch);
    // Cyan band
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: 0.45,
      fill: { color: C.cyan }, line: { type: 'none' },
    });
    s.addText(g.n, {
      x: x + 0.2, y: 1.85, w: cw - 0.4, h: 0.45,
      fontSize: 11, fontFace: F.head, color: C.bgDark, bold: true,
      align: 'left', valign: 'middle', margin: 0, charSpacing: 3,
    });
    s.addText(g.t, {
      x: x + 0.2, y: 2.45, w: cw - 0.4, h: 0.55,
      fontSize: 19, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(g.d, {
      x: x + 0.2, y: 3.1, w: cw - 0.4, h: 2.4,
      fontSize: 11.5, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });
    // Amber tag at bottom
    s.addShape(pres.shapes.RECTANGLE, {
      x: x + 0.2, y: 5.65, w: cw - 0.4, h: 0.7,
      fill: { color: '162E45' }, line: { color: C.amber, width: 1 },
    });
    s.addText(g.tag, {
      x: x + 0.3, y: 5.65, w: cw - 0.6, h: 0.7,
      fontSize: 10, fontFace: F.body, color: C.amber, italic: true,
      align: 'left', valign: 'middle', margin: 0,
    });
  });

  s.addText('Why four? — each gate catches a class of failure the others miss. A wave only ships when all four are green for every routine in it.', {
    x: 0.5, y: 6.85, w: SW - 1.0, h: 0.35,
    fontSize: 11.5, fontFace: F.body, color: C.cyan, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 9 — How Astra accelerates Delphi & C++ specifically
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'How Astra accelerates Delphi & C++',
    'Two production-grade pipelines — purpose-built for the quirks that trip a generic LLM.');

  // Two language columns
  const langs = [
    {
      label: 'DELPHI', accent: C.cyan,
      pipeline: 'Pascal parser  →  contract catalogue  →  starter kit',
      points: [
        { h: 'Production-grade Pascal parser',
          d: 'Tree-sitter grammar that knows TComponent ownership, ARC interfaces, properties, events, RTTI.' },
        { h: 'Behaviour rules tuned to Delphi traps',
          d: 'Five extra rule types capture object lifetime, interface implementation, property accessors, event handlers, and RTTI usage.' },
        { h: 'Starter kit for Indy / SMTP-family code',
          d: '.NET 10 project + xUnit + IDisposable mapping for ARC interfaces. Calibrated on a 60k-LOC Indy Sockets corpus.' },
      ],
    },
    {
      label: 'C++', accent: C.amber,
      pipeline: 'Clang parser  →  contract catalogue  →  starter kit',
      points: [
        { h: 'Production-grade C++ parser',
          d: 'Real Clang frontend — handles templates, lambdas, C++20 features, and preprocessor branches.' },
        { h: 'Behaviour rules tuned to C++ traps',
          d: 'Three extra rule types capture template instantiations, undefined-behaviour risks, and exception contracts.' },
        { h: 'Starter kit for fmt-family code',
          d: '.NET 10 project + xUnit + template lowering to params object?[]. Calibrated on a 14k-LOC fmtlib corpus.' },
      ],
    },
  ];

  const cw = 6.2, ch = 5.0, gap = 0.3;
  langs.forEach((L, i) => {
    const x = 0.55 + i * (cw + gap);
    card(s, x, 1.85, cw, ch);
    // Coloured label band
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: 0.45,
      fill: { color: L.accent }, line: { type: 'none' },
    });
    s.addText(L.label, {
      x: x + 0.2, y: 1.85, w: cw - 0.4, h: 0.45,
      fontSize: 13, fontFace: F.head, color: C.bgDark, bold: true,
      align: 'left', valign: 'middle', margin: 0, charSpacing: 3,
    });
    // Pipeline string
    s.addText(L.pipeline, {
      x: x + 0.2, y: 2.4, w: cw - 0.4, h: 0.4,
      fontSize: 12.5, fontFace: F.head, color: L.accent, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    // Three rows
    L.points.forEach((pt, j) => {
      const py = 2.95 + j * 0.75;
      s.addText(pt.h, {
        x: x + 0.2, y: py, w: cw - 0.4, h: 0.32,
        fontSize: 12, fontFace: F.head, color: C.white, bold: true,
        align: 'left', valign: 'middle', margin: 0,
      });
      s.addText(pt.d, {
        x: x + 0.2, y: py + 0.32, w: cw - 0.4, h: 0.45,
        fontSize: 10.5, fontFace: F.body, color: C.muted,
        align: 'left', valign: 'top', margin: 0,
      });
    });
  });

  s.addText('Default target: .NET 10 LTS (support horizon Nov 2028)  ·  Java/Spring also available  ·  Fortran/COBOL supported on the same engine', {
    x: 0.5, y: 7.0, w: SW - 1.0, h: 0.3,
    fontSize: 11, fontFace: F.body, color: C.faint, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 10 — AI Co-Pilots — where AI does the work
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'AI co-pilots — where AI does the work',
    'Astra orchestrates Claude as a sequence of specialised co-pilots. Humans review every artefact; AI handles the drudgery.');

  // Two-row diagram: per stage, who does what
  const rows = [
    { stage: 'Draft contract',   ai: 'Claude reads the Delphi/C++ routine + its callers and proposes every behaviour rule, with citations.', human: 'Engineer scans for obvious misses; SME accepts each rule, edits where Claude got it wrong.' },
    { stage: 'Generate code',    ai: 'Claude emits a buildable .NET 10 starter kit using the blueprint catalogue — class, tests, project file.', human: 'Engineer reviews the diff and either edits or routes back to the contract.' },
    { stage: 'Run gate 4',       ai: 'A property-based tester invents 100 inputs aimed at the contract\'s edge cases — looks for behaviour drift.', human: 'Engineer triages any failing input; SME decides if it\'s a real bug or a contract clarification.' },
    { stage: 'Decide what\'s next', ai: 'Astra surfaces dependency-aware ordering, blast radius, and SME workload — recommends the next wave.', human: 'Programme manager approves the wave; the pod accepts and starts the cycle again.' },
  ];

  const headerY = 1.75, rowH = 1.0, rgap = 0.08;
  // Column headers
  card(s, 0.55, headerY, 4.3, 0.5);
  s.addText('STAGE', { x: 0.75, y: headerY, w: 4.0, h: 0.5,
    fontSize: 12, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0, charSpacing: 3 });
  card(s, 4.95, headerY, 4.0, 0.5);
  s.addText('AI CO-PILOT DOES', { x: 5.15, y: headerY, w: 3.7, h: 0.5,
    fontSize: 12, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0, charSpacing: 3 });
  card(s, 9.05, headerY, 3.75, 0.5);
  s.addText('HUMAN DECIDES', { x: 9.25, y: headerY, w: 3.45, h: 0.5,
    fontSize: 12, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0, charSpacing: 3 });

  rows.forEach((r, i) => {
    const y = headerY + 0.5 + 0.1 + i * (rowH + rgap);
    card(s, 0.55, y, 4.3, rowH);
    s.addText(r.stage, {
      x: 0.75, y, w: 4.0, h: rowH,
      fontSize: 15, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    card(s, 4.95, y, 4.0, rowH);
    s.addText(r.ai, {
      x: 5.15, y: y + 0.1, w: 3.7, h: rowH - 0.2,
      fontSize: 11, fontFace: F.body, color: C.white,
      align: 'left', valign: 'top', margin: 0,
    });
    card(s, 9.05, y, 3.75, rowH);
    s.addText(r.human, {
      x: 9.25, y: y + 0.1, w: 3.45, h: rowH - 0.2,
      fontSize: 11, fontFace: F.body, color: C.white,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  // Footer summary
  s.addText('The harness deliberately keeps humans in the loop at every gate — speed without giving up auditability.', {
    x: 0.5, y: 7.0, w: SW - 1.0, h: 0.3,
    fontSize: 11.5, fontFace: F.body, color: C.cyan, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 11 — Pod structure
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'How we staff the programme — the 4-person pod',
    'One pod owns a slice of the portfolio end-to-end. Pods scale with the wave count.');

  const roles = [
    { count: '×1', t: 'Engineering Lead',
      d: 'Single-threaded owner of pod delivery.',
      acc: ['Throughput, velocity, on-time delivery', 'Architecture & key technical decisions', 'Coordinates with your team\'s tech leads'],
      directs: 'Pilot prioritisation · cross-pod orchestration',
    },
    { count: '×2', t: 'Migration Engineers',
      d: 'Contract authors & AI co-pilot directors.',
      acc: ['Drafts behaviour contracts via Astra', 'Reviews AI-emitted code, merges quality', 'Handles edge-case carve-outs'],
      directs: 'Contract Drafting · Test Gen · PR Review',
    },
    { count: '×1', t: 'Quality Engineer',
      d: 'Owns the quality signal — not manual testing.',
      acc: ['Wave-readiness sign-off', 'Test pack coverage standards', 'Defect-escape rate after cutover'],
      directs: 'Test Suite Gen · 4-gate runs · Triage',
    },
  ];

  const cw = 4.05, ch = 4.4, gap = 0.18;
  const totalW = cw * 3 + gap * 2;
  const startX = (SW - totalW) / 2;
  roles.forEach((r, i) => {
    const x = startX + i * (cw + gap);
    card(s, x, 1.85, cw, ch);
    // Header band
    s.addShape(pres.shapes.RECTANGLE, {
      x, y: 1.85, w: cw, h: 0.55,
      fill: { color: C.bgPanel }, line: { type: 'none' },
    });
    s.addText(r.t, {
      x: x + 0.2, y: 1.85, w: cw - 1.1, h: 0.55,
      fontSize: 15, fontFace: F.head, color: C.cyan, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    // Count badge top right
    s.addShape(pres.shapes.RECTANGLE, {
      x: x + cw - 0.85, y: 1.95, w: 0.7, h: 0.35,
      fill: { color: C.cyan }, line: { type: 'none' },
    });
    s.addText(r.count, {
      x: x + cw - 0.85, y: 1.95, w: 0.7, h: 0.35,
      fontSize: 12, fontFace: F.head, color: C.bgDark, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(r.d, {
      x: x + 0.2, y: 2.55, w: cw - 0.4, h: 0.45,
      fontSize: 12.5, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'top', margin: 0,
    });
    s.addText('ACCOUNTABLE FOR', {
      x: x + 0.2, y: 3.1, w: cw - 0.4, h: 0.3,
      fontSize: 9.5, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0, charSpacing: 2,
    });
    s.addText(r.acc.map((a, k) => ({ text: a, options: { bullet: true, breakLine: k < r.acc.length - 1 } })), {
      x: x + 0.2, y: 3.4, w: cw - 0.4, h: 1.5,
      fontSize: 11, fontFace: F.body, color: C.white,
      align: 'left', valign: 'top', margin: 0, paraSpaceAfter: 3,
    });
    s.addText('CO-PILOTS', {
      x: x + 0.2, y: 5.5, w: cw - 0.4, h: 0.3,
      fontSize: 9.5, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0, charSpacing: 2,
    });
    s.addText(r.directs, {
      x: x + 0.2, y: 5.8, w: cw - 0.4, h: 0.4,
      fontSize: 10.5, fontFace: F.body, color: C.muted, italic: true,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  // Accountability principle band
  s.addShape(pres.shapes.RECTANGLE, {
    x: 0.5, y: 6.5, w: SW - 1.0, h: 0.6,
    fill: { color: C.bgPanel }, line: { type: 'none' },
  });
  s.addText('Single-vendor accountability  —  one pod, one outcome, one signature on every wave.', {
    x: 0.5, y: 6.5, w: SW - 1.0, h: 0.6,
    fontSize: 14, fontFace: F.head, color: C.white, bold: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 12 — Programme timeline & outcomes
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'Programme timeline & outcomes',
    'Indicative for a ~700-routine portfolio. Actuals re-baselined after Phase 1 discovery.');

  // Timeline header band
  card(s, 0.5, 1.85, SW - 1.0, 0.5);
  s.addText('MONTH', {
    x: 0.7, y: 1.85, w: 1.5, h: 0.5,
    fontSize: 10, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0, charSpacing: 3,
  });
  const months = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];
  const tlX = 2.5, tlW = SW - 1.0 - 2.0;
  months.forEach((m, i) => {
    s.addText(m, {
      x: tlX + i * (tlW / months.length), y: 1.85, w: tlW / months.length, h: 0.5,
      fontSize: 12, fontFace: F.head, color: C.amber, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
  });

  // Phase bars (Gantt-ish)
  const bars = [
    { label: 'Phase 1 — Discovery',          start: 0, len: 1.0, color: C.cyan },
    { label: 'Phase 2 — Pilot',              start: 1.0, len: 1.5, color: C.cyan },
    { label: 'Phase 3 — Wave-by-wave',       start: 2.5, len: 5.0, color: C.amber },
    { label: 'Phase 4 — Validation/Cutover (rolling)', start: 3.5, len: 4.0, color: C.green },
    { label: 'Phase 5 — Run & KT',           start: 7.5, len: 1.5, color: C.cyan },
  ];
  const monthW = tlW / 9;
  const barY = 2.55, barH = 0.45, barGap = 0.15;
  bars.forEach((b, i) => {
    const y = barY + i * (barH + barGap);
    s.addText(b.label, {
      x: 0.55, y, w: 1.95, h: barH,
      fontSize: 10.5, fontFace: F.body, color: C.white, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x: tlX + b.start * monthW, y: y + 0.05, w: b.len * monthW - 0.05, h: barH - 0.1,
      fill: { color: b.color }, line: { type: 'none' },
    });
  });

  // Outcomes panel
  card(s, 0.5, 5.6, SW - 1.0, 1.4);
  s.addText('Outcomes you can show your board', {
    x: 0.7, y: 5.7, w: SW - 1.4, h: 0.4,
    fontSize: 14, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  // 4 outcome bullets in a row
  const outcomes = [
    { v: '~9 mo',  l: 'end-to-end vs 24+ mo manual' },
    { v: '700+',  l: 'routines migrated, all 4 gates green' },
    { v: '100%',  l: 'behaviour rules cite signed evidence' },
    { v: 'Zero',  l: 'half-migrated states — wave-by-wave' },
  ];
  const ow = (SW - 1.4) / 4;
  outcomes.forEach((o, i) => {
    const x = 0.7 + i * ow;
    s.addText(o.v, {
      x, y: 6.1, w: ow, h: 0.5,
      fontSize: 24, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addText(o.l, {
      x, y: 6.6, w: ow - 0.2, h: 0.35,
      fontSize: 10, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 13 — AS-IS vs TO-BE
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  chrome(s, 'AS-IS vs TO-BE',
    'A side-by-side view of the change — at a routine level and at a programme level.');

  // Two column headers
  card(s, 0.55, 1.85, 6.0, 0.55, '6B2727');
  s.addText('AS-IS  —  Manual Delphi/C++ rewrite', {
    x: 0.55, y: 1.85, w: 6.0, h: 0.55,
    fontSize: 14, fontFace: F.head, color: C.white, bold: true,
    align: 'center', valign: 'middle', margin: 0,
  });
  card(s, 6.75, 1.85, 6.0, 0.55, '1F5F46');
  s.addText('TO-BE  —  Nous Astra-accelerated programme', {
    x: 6.75, y: 1.85, w: 6.0, h: 0.55,
    fontSize: 14, fontFace: F.head, color: C.white, bold: true,
    align: 'center', valign: 'middle', margin: 0,
  });

  const rows = [
    ['Contract written by hand from messy Delphi/C++; SMEs grilled in workshops', 'AI co-pilot drafts the contract with source-line citations; SME signs each rule'],
    ['Behaviour parity assumed — drift surfaces in production', 'Four independent quality gates prove parity before any cutover'],
    ['Migration order guessed; surprises break the schedule', 'Dependency-aware wave planning — leaves migrate first, risk-first within each wave'],
    ['Audit trail re-constructed after the fact, if at all', 'Every decision logged at the source; cutover packet ready on day one'],
    ['No re-runs — every change costs another sprint', 'Re-run a wave when a rule changes; the code re-emits automatically'],
    ['Throughput scales linearly with headcount', '2-3× throughput at roughly half the headcount of a manual rewrite'],
  ];

  const rowH = 0.62, rowGap = 0.06;
  rows.forEach((r, i) => {
    const y = 2.55 + i * (rowH + rowGap);
    // Left: red X + text
    card(s, 0.55, y, 6.0, rowH);
    s.addText('✗', {
      x: 0.7, y, w: 0.4, h: rowH,
      fontSize: 16, fontFace: F.head, color: C.red, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(r[0], {
      x: 1.15, y, w: 5.3, h: rowH,
      fontSize: 11, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'middle', margin: 0,
    });
    // Right: green check + text
    card(s, 6.75, y, 6.0, rowH);
    s.addText('✓', {
      x: 6.9, y, w: 0.4, h: rowH,
      fontSize: 16, fontFace: F.head, color: C.green, bold: true,
      align: 'center', valign: 'middle', margin: 0,
    });
    s.addText(r[1], {
      x: 7.35, y, w: 5.3, h: rowH,
      fontSize: 11, fontFace: F.body, color: C.white,
      align: 'left', valign: 'middle', margin: 0,
    });
  });
}

// ═══════════════════════════════════════════════════════════════════════════
// Slide 14 — Why Nous (close)
// ═══════════════════════════════════════════════════════════════════════════
{
  const s = pres.addSlide();
  s.background = { color: C.bgDark };

  // Top-right brand — single horizontal block to avoid ® wrapping
  s.addText([
    { text: 'NOUS ', options: { fontSize: 18, bold: true } },
    { text: 'INFOSYSTEMS', options: { fontSize: 10, charSpacing: 2 } },
  ], {
    x: SW - 3.5, y: 0.35, w: 3.1, h: 0.4,
    fontFace: F.head, color: C.white,
    align: 'right', valign: 'middle', margin: 0,
  });

  s.addText('Why Nous for this programme', {
    x: 0.7, y: 1.0, w: SW - 1.4, h: 0.8,
    fontSize: 40, fontFace: F.head, color: C.cyan, bold: true,
    align: 'left', valign: 'middle', margin: 0,
  });
  s.addText('Three things that are hard to assemble separately — and that we bring as one engagement.', {
    x: 0.7, y: 1.85, w: SW - 1.4, h: 0.45,
    fontSize: 16, fontFace: F.head, color: C.amber, italic: true,
    align: 'left', valign: 'middle', margin: 0,
  });

  const reasons = [
    { tag: '01', t: 'A purpose-built accelerator',
      d: 'Astra was built for legacy-language migration — not adapted from a generic AI workflow. Delphi and C++ have been first-class inputs since day one, with their own parsers, behaviour-rule catalogues, and starter kits.' },
    { tag: '02', t: 'A delivery model that owns the outcome',
      d: 'One pod, one outcome, one signature. Single-vendor accountability across discovery, migration, validation, cutover, and run. No finger-pointing across vendors when the programme slips.' },
    { tag: '03', t: 'An audit-grade trail from day one',
      d: 'Spec-driven means every line of new code traces to a signed contract and a Delphi/C++ source line. Compliance and risk teams get what they need without a separate paperwork project.' },
  ];

  const cw = 4.0, ch = 4.5, gap = 0.2;
  const totalW = cw * 3 + gap * 2;
  const startX = (SW - totalW) / 2;
  reasons.forEach((r, i) => {
    const x = startX + i * (cw + gap);
    card(s, x, 2.6, cw, ch);
    s.addText(r.tag, {
      x: x + 0.3, y: 2.8, w: 1.2, h: 0.6,
      fontSize: 28, fontFace: F.head, color: C.amber, bold: true,
      align: 'left', valign: 'middle', margin: 0,
    });
    s.addShape(pres.shapes.RECTANGLE, {
      x: x + 0.3, y: 3.4, w: 0.8, h: 0.05,
      fill: { color: C.cyan }, line: { type: 'none' },
    });
    s.addText(r.t, {
      x: x + 0.3, y: 3.55, w: cw - 0.5, h: 0.9,
      fontSize: 17, fontFace: F.head, color: C.white, bold: true,
      align: 'left', valign: 'top', margin: 0,
    });
    s.addText(r.d, {
      x: x + 0.3, y: 4.5, w: cw - 0.5, h: 2.5,
      fontSize: 12, fontFace: F.body, color: C.muted,
      align: 'left', valign: 'top', margin: 0,
    });
  });

  s.addText('Ready when you are.   ·   Let\'s scope a 4-week Discovery on your most representative subsystem.', {
    x: 0.7, y: 7.0, w: SW - 1.4, h: 0.35,
    fontSize: 13, fontFace: F.head, color: C.cyan, bold: true, italic: true,
    align: 'center', valign: 'middle', margin: 0,
  });
}

// ── Write ────────────────────────────────────────────────────────────────
const OUTPATH = 'C:/Astra RE Harness/app/e2e/demo-output/delphi-cpp-migration-programme.pptx';
pres.writeFile({ fileName: OUTPATH }).then((p) => console.log(`Wrote: ${p}`));
