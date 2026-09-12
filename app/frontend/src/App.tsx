import { useCallback, useEffect, useState, type ReactElement } from 'react';
import { Route, Routes, useNavigate } from 'react-router-dom';
import { TopBar } from '@/shell/TopBar';
import { LeftNav } from '@/shell/LeftNav';
import { MobileNav } from '@/shell/MobileNav';
import { LegacyView } from '@/shell/LegacyView';
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
import { KeyboardOverlay } from '@/components/KeyboardOverlay';

/** Pre-v2 pages render inside the light wrapper until Increment 2 restyles them. */
const legacy = (page: ReactElement) => <LegacyView>{page}</LegacyView>;

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
            <Route path="/home" element={legacy(<HomePage />)} />
            <Route path="/system" element={legacy(<SystemPage />)} />
            {/* User-facing routes use /projects; /corpora kept as legacy
                aliases so existing e2e tests and bookmarks still resolve. */}
            <Route path="/projects" element={legacy(<CorporaPage />)} />
            <Route path="/projects/new" element={legacy(<NewCorpusPage />)} />
            <Route path="/projects/:id" element={legacy(<CorpusDetailPage />)} />
            <Route path="/corpora" element={legacy(<CorporaPage />)} />
            <Route path="/corpora/new" element={legacy(<NewCorpusPage />)} />
            <Route path="/corpora/:id" element={legacy(<CorpusDetailPage />)} />
            <Route path="/corpora/:id/dependency-graph" element={legacy(<DependencyGraphPage />)} />
            <Route path="/corpora/:id/migration-plan" element={legacy(<MigrationPlanPage />)} />
            <Route path="/corpora/:id/docs" element={legacy(<DocsPage />)} />
            <Route path="/projects/:id/docs" element={legacy(<DocsPage />)} />
            <Route path="/corpora/:id/pattern-analysis" element={legacy(<PatternAnalysisPage />)} />
            <Route path="/projects/:id/pattern-analysis" element={legacy(<PatternAnalysisPage />)} />
            <Route path="/subroutines" element={legacy(<SubroutinesPage />)} />
            <Route path="/subroutines/:id" element={legacy(<SubroutineDetailPage />)} />
            <Route path="/subroutines/:id/extract" element={legacy(<LiveExtractionPage />)} />
            <Route path="/subroutines/:id/spec" element={legacy(<DraftSpecPage />)} />
            <Route path="/subroutines/:id/review" element={legacy(<SpecReviewPage />)} />
            <Route path="/specs/:id/audit" element={legacy(<AuditTrailPage />)} />
            <Route path="/specs/:id/scaffold" element={legacy(<LiveScaffoldPage />)} />
            <Route path="/scaffolds" element={legacy(<ScaffoldsPage />)} />
            <Route path="/scaffolds/:id" element={legacy(<ScaffoldArtifactPage />)} />
            <Route path="/scaffolds/:id/validation" element={legacy(<ValidationReportPage />)} />
            <Route path="/my-reviews" element={legacy(<MyReviewsPage />)} />
            <Route path="/comments" element={legacy(<CommentsPage />)} />
            <Route path="/compliance" element={legacy(<CompliancePage />)} />
            <Route path="/platform" element={legacy(<PlatformIndexPage />)} />
            <Route path="/platform/prompts" element={legacy(<PromptCatalogPage />)} />
            <Route path="/platform/golden-dataset" element={legacy(<GoldenDatasetPage />)} />
            <Route path="/platform/harmonisation" element={legacy(<HarmonisationPage />)} />
            <Route path="/platform/portfolio" element={legacy(<PortfolioDashboardPage />)} />
            <Route path="/platform/languages" element={legacy(<LanguagesPage />)} />
            <Route path="/platform/roles" element={legacy(<RolesPage />)} />
            <Route path="/platform/validation" element={legacy(<ValidationPolicyPage />)} />
            <Route path="/platform/llm" element={legacy(<LlmSettingsPage />)} />
            <Route path="/platform/signatures" element={legacy(<SignatureHealthPage />)} />
            <Route path="*" element={legacy(<NotFoundPage />)} />
          </Routes>
        </main>
      </div>
      <KeyboardOverlay open={helpOpen} onClose={closeHelp} />
      <CommandPalette />
    </div>
  );
}
