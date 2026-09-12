import { useCallback, useEffect, useState } from 'react';
import { Route, Routes, useNavigate } from 'react-router-dom';
import { TopBar } from '@/shell/TopBar';
import { LeftNav } from '@/shell/LeftNav';
import { MobileNav } from '@/shell/MobileNav';
import { WorkspacePage } from '@/workspace/WorkspacePage';
import { CommandPalette } from '@/copilot/CommandPalette';
import { HomePage } from '@/pages/HomePage';
import { SystemPage } from '@/pages/SystemPage';
import { CorporaPage } from '@/pages/CorporaPage';
import { NewCorpusPage } from '@/pages/NewCorpusPage';
import { CorpusDetailPage } from '@/pages/CorpusDetailPage';
import { SubroutinesPage } from '@/pages/SubroutinesPage';
import { SubroutineDetailPage } from '@/pages/SubroutineDetailPage';
import { LiveExtractionPage } from '@/pages/LiveExtractionPage';
import { DraftSpecPage } from '@/pages/DraftSpecPage';
import { SpecReviewPage } from '@/pages/SpecReviewPage';
import { MyReviewsPage } from '@/pages/MyReviewsPage';
import { CommentsPage } from '@/pages/CommentsPage';
import { AuditTrailPage } from '@/pages/AuditTrailPage';
import { LiveScaffoldPage } from '@/pages/LiveScaffoldPage';
import { ScaffoldArtifactPage } from '@/pages/ScaffoldArtifactPage';
import { ScaffoldsPage } from '@/pages/ScaffoldsPage';
import { ValidationReportPage } from '@/pages/ValidationReportPage';
import { CompliancePage } from '@/pages/CompliancePage';
import { PlatformIndexPage } from '@/pages/PlatformIndexPage';
import { PromptCatalogPage } from '@/pages/PromptCatalogPage';
import { GoldenDatasetPage } from '@/pages/GoldenDatasetPage';
import { HarmonisationPage } from '@/pages/HarmonisationPage';
import { DependencyGraphPage } from '@/pages/DependencyGraphPage';
import { MigrationPlanPage } from '@/pages/MigrationPlanPage';
import { PortfolioDashboardPage } from '@/pages/PortfolioDashboardPage';
import { LanguagesPage } from '@/pages/LanguagesPage';
import { RolesPage } from '@/pages/RolesPage';
import { ValidationPolicyPage } from '@/pages/ValidationPolicyPage';
import { LlmSettingsPage } from '@/pages/LlmSettingsPage';
import { SignatureHealthPage } from '@/pages/SignatureHealthPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { DocsPage } from '@/pages/DocsPage';
import { PatternAnalysisPage } from '@/pages/PatternAnalysisPage';
import { AssessmentPage } from '@/pages/AssessmentPage';
import { KeyboardOverlay } from '@/components/KeyboardOverlay';

export function App() {
  const [helpOpen, setHelpOpen] = useState(false);
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const navigate = useNavigate();

  const openHelp = useCallback(() => setHelpOpen(true), []);
  const closeHelp = useCallback(() => setHelpOpen(false), []);
  const openMobileNav = useCallback(() => setMobileNavOpen(true), []);
  const closeMobileNav = useCallback(() => setMobileNavOpen(false), []);

  useEffect(() => {
    let chord: string | null = null;
    let chordTimer: ReturnType<typeof setTimeout> | null = null;

    const handler = (e: KeyboardEvent) => {
      // Skip when the user is typing in an input/textarea/contenteditable.
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;

      // ? — open keyboard help (Shift+/ on US layouts)
      if (e.key === '?' && !e.metaKey && !e.ctrlKey) {
        e.preventDefault();
        openHelp();
        return;
      }

      // g h / g s — navigation chord
      if (chord === 'g') {
        if (e.key === 'h') {
          e.preventDefault();
          navigate('/');
        } else if (e.key === 's') {
          e.preventDefault();
          navigate('/system');
        }
        chord = null;
        if (chordTimer) clearTimeout(chordTimer);
        return;
      }
      if (e.key === 'g') {
        chord = 'g';
        chordTimer = setTimeout(() => {
          chord = null;
        }, 800);
      }
    };

    window.addEventListener('keydown', handler);
    return () => {
      window.removeEventListener('keydown', handler);
      if (chordTimer) clearTimeout(chordTimer);
    };
  }, [navigate, openHelp]);

  return (
    <div className="flex h-screen flex-col bg-canvas text-ink-primary">
      <a
        href="#main-content"
        className="sr-only z-50 rounded-md bg-volt px-4 py-2 text-body font-medium text-on-volt focus:not-sr-only focus:absolute focus:left-3 focus:top-2"
      >
        Skip to content
      </a>
      <TopBar onOpenHelp={openHelp} onOpenNav={openMobileNav} />
      <MobileNav open={mobileNavOpen} onClose={closeMobileNav} />
      <div className="flex min-h-0 flex-1">
        <LeftNav />
        {/* <main> is the scroll container: the Workspace fills it (h-full);
            legacy pages scroll inside it under the fixed chrome. */}
        <main id="main-content" tabIndex={-1} className="min-h-0 min-w-0 flex-1 overflow-y-auto bg-canvas">
          <Routes>
            {/* The conversation is the primary surface. */}
            <Route path="/" element={<WorkspacePage />} />
            <Route path="/w/:conversationId" element={<WorkspacePage />} />
            <Route path="/home" element={<HomePage />} />
            <Route path="/system" element={<SystemPage />} />
            {/* User-facing routes use /projects; /corpora kept as legacy
                aliases so existing e2e tests and bookmarks still resolve. */}
            <Route path="/projects" element={<CorporaPage />} />
            <Route path="/projects/new" element={<NewCorpusPage />} />
            <Route path="/projects/:id" element={<CorpusDetailPage />} />
            <Route path="/corpora" element={<CorporaPage />} />
            <Route path="/corpora/new" element={<NewCorpusPage />} />
            <Route path="/corpora/:id" element={<CorpusDetailPage />} />
            <Route path="/corpora/:id/dependency-graph" element={<DependencyGraphPage />} />
            <Route path="/corpora/:id/migration-plan" element={<MigrationPlanPage />} />
            <Route path="/corpora/:id/docs" element={<DocsPage />} />
            <Route path="/projects/:id/docs" element={<DocsPage />} />
            <Route path="/corpora/:id/pattern-analysis" element={<PatternAnalysisPage />} />
            <Route path="/projects/:id/pattern-analysis" element={<PatternAnalysisPage />} />
            {/* WS2 Inc 2 — dark-first, no legacy wrapper. */}
            <Route path="/projects/:id/assessment" element={<AssessmentPage />} />
            <Route path="/corpora/:id/assessment" element={<AssessmentPage />} />
            <Route path="/subroutines" element={<SubroutinesPage />} />
            <Route path="/subroutines/:id" element={<SubroutineDetailPage />} />
            <Route path="/subroutines/:id/extract" element={<LiveExtractionPage />} />
            <Route path="/subroutines/:id/spec" element={<DraftSpecPage />} />
            <Route path="/subroutines/:id/review" element={<SpecReviewPage />} />
            <Route path="/specs/:id/audit" element={<AuditTrailPage />} />
            <Route path="/specs/:id/scaffold" element={<LiveScaffoldPage />} />
            <Route path="/scaffolds" element={<ScaffoldsPage />} />
            <Route path="/scaffolds/:id" element={<ScaffoldArtifactPage />} />
            <Route path="/scaffolds/:id/validation" element={<ValidationReportPage />} />
            <Route path="/my-reviews" element={<MyReviewsPage />} />
            <Route path="/comments" element={<CommentsPage />} />
            <Route path="/compliance" element={<CompliancePage />} />
            <Route path="/platform" element={<PlatformIndexPage />} />
            <Route path="/platform/prompts" element={<PromptCatalogPage />} />
            <Route path="/platform/golden-dataset" element={<GoldenDatasetPage />} />
            <Route path="/platform/harmonisation" element={<HarmonisationPage />} />
            <Route path="/platform/portfolio" element={<PortfolioDashboardPage />} />
            <Route path="/platform/languages" element={<LanguagesPage />} />
            <Route path="/platform/roles" element={<RolesPage />} />
            <Route path="/platform/validation" element={<ValidationPolicyPage />} />
            <Route path="/platform/llm" element={<LlmSettingsPage />} />
            <Route path="/platform/signatures" element={<SignatureHealthPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </main>
      </div>
      <KeyboardOverlay open={helpOpen} onClose={closeHelp} />
      <CommandPalette />
    </div>
  );
}
