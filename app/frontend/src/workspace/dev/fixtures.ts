/**
 * Fixture artifacts for the Increment 2 cards. Dev-only: rendered by the
 * card gallery behind `?fixture=1` on the assessment page so a card can be
 * eyeballed before the backend emits it. Shapes mirror ws2-inc2-contract.md.
 */
import type { Artifact } from '@/lib/conversations';

const CORPUS_ID = '00000000-0000-4000-8000-00000000f0f0';

export const ASSESSMENT_FIXTURE: Artifact = {
  kind: 'assessment',
  refId: CORPUS_ID,
  props: {
    corpusName: 'fmt (Fortran demo)',
    sourceLanguage: 'fortran-f77',
    generatedAt: new Date(Date.now() - 6 * 60_000).toISOString(),
    inventory: {
      routines: 142,
      files: 37,
      loc: 18_420,
      languages: [
        { language: 'fortran-f77', routines: 131 },
        { language: 'cpp', routines: 11 },
      ],
    },
    hotspots: [
      { id: '11111111-1111-4111-8111-111111111111', name: 'FMTSUB', callers: 27, transitiveCallers: 96, inCycle: true },
      { id: '22222222-2222-4222-8222-222222222222', name: 'WRTREC', callers: 19, transitiveCallers: 61, inCycle: false },
      { id: '33333333-3333-4333-8333-333333333333', name: 'PRSLIN', callers: 14, transitiveCallers: 44, inCycle: true },
      { id: '', name: 'ERRHND', callers: 12, transitiveCallers: 38, inCycle: false },
    ],
    cycles: 3,
    patterns: {
      clusters: 14,
      multiMember: 9,
      singletons: 5,
      analysed: true,
      topClusters: [
        { label: 'Record formatting', memberCount: 23 },
        { label: 'ISAM read/write', memberCount: 17 },
        { label: 'Numeric conversion', memberCount: 11 },
      ],
    },
    effort: {
      score: 3,
      band: 'L',
      personWeeks: { low: 14, high: 22 },
      drivers: ['142 routines across 37 files', '3 call cycles need untangling before waves can be ordered', 'shared COMMON blocks in 41% of routines'],
    },
    risk: {
      score: 4,
      band: 'XL',
      drivers: ['18% of routines are in call cycles — waves cannot be cleanly ordered', 'one routine is transitively called by 68% of the estate', 'heavy shared-storage coupling (COMMON blocks / globals / copybooks)'],
    },
    recommendation: {
      mode: 'strangler',
      targetStack: 'dotnet8',
      summary:
        'Wrap the formatting core behind a façade and migrate the leaf routines first; the FMTSUB cycle is the last thing to move. Equivalence-test every wave against the golden dataset.',
    },
    sectionId: '44444444-4444-4444-8444-444444444444',
    href: `/projects/${CORPUS_ID}/assessment`,
  },
};

export const PLAN_WAVES_FIXTURE: Artifact = {
  kind: 'planWaves',
  refId: '55555555-5555-4555-8555-555555555555',
  props: {
    corpusId: CORPUS_ID,
    status: 'approved',
    strategyName: 'leaf-first',
    totalWaves: 5,
    totalRoutines: 142,
    summary: 'Leaf routines first, then the formatting core; the FMTSUB cycle moves last as one unit.',
    waves: [
      { waveNumber: 1, name: 'Leaf utilities', routineCount: 38, status: 'completed' },
      { waveNumber: 2, name: 'Numeric conversion', routineCount: 29, status: 'completed' },
      { waveNumber: 3, name: 'Record I/O', routineCount: 31, status: 'in_progress' },
      { waveNumber: 4, name: 'Parsing', routineCount: 26, status: 'planned' },
      { waveNumber: 5, name: 'Formatting core (cycle)', routineCount: 18, status: 'planned' },
    ],
  },
};

export const DOC_SECTION_FIXTURE: Artifact = {
  kind: 'docSection',
  refId: '66666666-6666-4666-8666-666666666666',
  props: {
    title: 'Module overview — Record I/O',
    kind: 'module-overview',
    corpusId: CORPUS_ID,
    href: `/projects/${CORPUS_ID}/docs`,
    markdown: [
      '## Purpose',
      '',
      'The Record I/O module owns every ISAM read and write in the estate. Callers never touch file handles directly; they go through `WRTREC` and `RDREC`, which own locking and retry.',
      '',
      '## Key routines',
      '',
      '| Routine | Role | Callers |',
      '|---|---|---|',
      '| `WRTREC` | Write a record with lock + retry | 19 |',
      '| `RDREC` | Read a record by key | 16 |',
      '| `LCKREC` | Advisory lock | 7 |',
      '',
      '## Risks',
      '',
      '- Retry loops assume a 5-second lock timeout that is configured per site.',
      '- `WRTREC` and `PRSLIN` form a call cycle through the error handler.',
    ].join('\n'),
  },
};

export const RUN_RESUMABLE_FIXTURE: Artifact = {
  kind: 'runProgress',
  refId: '77777777-7777-4777-8777-777777777777',
  props: {
    kind: 'pattern-analysis',
    corpusId: CORPUS_ID,
    label: 'Pattern analysis · fmt',
    agent: 'discovery',
    state: 'RESUMABLE',
    startedAt: new Date(Date.now() - 25 * 60_000).toISOString(),
    stage: 'Clustering (2/3)',
    done: 96,
    total: 142,
    summary: 'Paused after the rate limit tripped; 96 of 142 digests are in.',
  },
};

export const SPEC_SUMMARY_FIXTURE: Artifact = {
  kind: 'specSummary',
  refId: '88888888-8888-4888-8888-888888888888',
  props: {
    subroutineId: '11111111-1111-4111-8111-111111111111',
    routineName: 'FMTSUB',
    state: 'IN_REVIEW',
    summary: 'Formats a numeric field into a fixed-width record slot, padding on the left and raising ERRHND on overflow.',
    counts: { invariants: 3, sideEffects: 1, edgeCases: 2, openQuestions: 1, total: 7, reviewed: 2 },
    sectionLabels: { invariants: 'Invariants', side_effects: 'Side effects', edge_cases: 'Edge cases', open_questions: 'Open questions' },
    sourceFilePath: 'src/fmt/fmtsub.f',
    lineStart: 112,
    lineEnd: 190,
    canRoute: false,
    canReview: true,
    canSign: false,
    claims: [
      { section: 'invariants', id: 'INV-1', text: 'The output slot is always exactly WIDTH characters wide.', review: 'accept', citation: 'L120-134', citationLines: '120-134' },
      { section: 'invariants', id: 'INV-2', text: 'Negative values keep their sign in the leftmost position.', review: null, citation: 'L136-141', citationLines: '136-141' },
      { section: 'invariants', id: 'INV-3', text: 'The input buffer is never modified.', review: 'accept', citation: null, citationLines: null },
      { section: 'side_effects', id: 'SE-1', text: 'On overflow the routine calls ERRHND with code 7 and returns a slot of asterisks.', review: null, citation: 'L160-171', citationLines: '160-171' },
      { section: 'edge_cases', id: 'EC-1', text: 'A WIDTH of zero returns immediately without touching the record.', review: null, citation: 'L114', citationLines: '114' },
      { section: 'edge_cases', id: 'EC-2', text: 'Values wider than WIDTH after rounding are treated as overflow.', review: null, citation: 'L150-158', citationLines: '150-158' },
      { section: 'open_questions', id: 'Q-1', text: 'Is the asterisk fill deliberate or a leftover from the VAX port?', review: null, citation: 'L165', citationLines: '165' },
    ],
    signedAt: null,
    signerDisplay: null,
  },
};

export const ALL_FIXTURES: Artifact[] = [
  ASSESSMENT_FIXTURE,
  PLAN_WAVES_FIXTURE,
  DOC_SECTION_FIXTURE,
  RUN_RESUMABLE_FIXTURE,
  SPEC_SUMMARY_FIXTURE,
];
