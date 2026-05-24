import { useCallback, useEffect, useState } from 'react';
import { Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { TopBar } from '@/shell/TopBar';
import { LeftNav } from '@/shell/LeftNav';
import { Breadcrumb } from '@/shell/Breadcrumb';
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
import { ValidationReportPage } from '@/pages/ValidationReportPage';
import { CompliancePage } from '@/pages/CompliancePage';
import { PlatformIndexPage } from '@/pages/PlatformIndexPage';
import { PromptCatalogPage } from '@/pages/PromptCatalogPage';
import { GoldenDatasetPage } from '@/pages/GoldenDatasetPage';
import { HarmonisationPage } from '@/pages/HarmonisationPage';
import { DependencyGraphPage } from '@/pages/DependencyGraphPage';
import { MigrationPlanPage } from '@/pages/MigrationPlanPage';
import { LanguagesPage } from '@/pages/LanguagesPage';
import { RolesPage } from '@/pages/RolesPage';
import { ValidationPolicyPage } from '@/pages/ValidationPolicyPage';
import { SignatureHealthPage } from '@/pages/SignatureHealthPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { KeyboardOverlay } from '@/components/KeyboardOverlay';

function buildBreadcrumbs(pathname: string): { label: string; href?: string }[] {
  // "Projects" is the user-facing word; URLs use /projects with /corpora kept
  // as a legacy alias so old bookmarks + the e2e suite still resolve.
  const projectsLink = { label: 'Projects', href: '/projects' };
  if (pathname === '/') return [{ label: 'Home' }];
  if (pathname === '/system') return [{ label: 'Home', href: '/' }, { label: 'System' }];
  if (pathname === '/projects' || pathname === '/corpora')
    return [{ label: 'Home', href: '/' }, { label: 'Projects' }];
  if (pathname === '/projects/new' || pathname === '/corpora/new')
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'New project' },
    ];
  if (pathname.startsWith('/projects/') || pathname.startsWith('/corpora/'))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Project' },
    ];
  if (pathname.match(/^\/subroutines\/[^/]+\/extract$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Subroutine' },
      { label: 'Extract' },
    ];
  if (pathname.match(/^\/subroutines\/[^/]+\/spec$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Subroutine' },
      { label: 'Draft spec' },
    ];
  if (pathname.match(/^\/subroutines\/[^/]+\/review$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Subroutine' },
      { label: 'Spec review' },
    ];
  if (pathname.match(/^\/specs\/[^/]+\/audit$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Spec' },
      { label: 'Audit trail' },
    ];
  if (pathname.match(/^\/specs\/[^/]+\/scaffold$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Spec' },
      { label: 'Generate scaffold' },
    ];
  if (pathname.match(/^\/scaffolds\/[^/]+\/validation$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Scaffold artifact' },
      { label: 'Validation' },
    ];
  if (pathname.match(/^\/scaffolds\/[^/]+$/))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Scaffold artifact' },
    ];
  if (pathname === '/my-reviews')
    return [{ label: 'Home', href: '/' }, { label: 'My reviews' }];
  if (pathname === '/comments')
    return [{ label: 'Home', href: '/' }, { label: 'Comments' }];
  if (pathname === '/compliance')
    return [{ label: 'Home', href: '/' }, { label: 'Compliance' }];
  if (pathname === '/platform')
    return [{ label: 'Home', href: '/' }, { label: 'Platform' }];
  if (pathname.startsWith('/platform/'))
    return [
      { label: 'Home', href: '/' },
      { label: 'Platform', href: '/platform' },
      { label: prettyPlatformLeaf(pathname) },
    ];
  if (pathname === '/subroutines')
    return [{ label: 'Home', href: '/' }, { label: 'Subroutines' }];
  if (pathname.startsWith('/subroutines/'))
    return [
      { label: 'Home', href: '/' },
      projectsLink,
      { label: 'Subroutine' },
    ];
  return [{ label: 'Home', href: '/' }];
}

function prettyPlatformLeaf(pathname: string): string {
  const last = pathname.slice('/platform/'.length).split('/')[0] ?? '';
  switch (last) {
    case 'prompts':    return 'Prompt Catalog';
    case 'languages':  return 'Languages';
    case 'validation': return 'Validation Policy';
    case 'signatures': return 'Signature Health';
    case 'roles':      return 'Roles & Permissions';
    default:           return last.charAt(0).toUpperCase() + last.slice(1);
  }
}

export function App() {
  const [helpOpen, setHelpOpen] = useState(false);
  const location = useLocation();
  const navigate = useNavigate();

  const openHelp = useCallback(() => setHelpOpen(true), []);
  const closeHelp = useCallback(() => setHelpOpen(false), []);

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

  const crumbs = buildBreadcrumbs(location.pathname);

  return (
    <div className="flex min-h-screen flex-col">
      <TopBar onOpenHelp={openHelp} />
      <div className="flex flex-1">
        <LeftNav />
        <main className="min-w-0 flex-1 bg-canvas">
          <Breadcrumb items={crumbs} />
          <Routes>
            <Route path="/" element={<HomePage />} />
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
            <Route path="/subroutines" element={<SubroutinesPage />} />
            <Route path="/subroutines/:id" element={<SubroutineDetailPage />} />
            <Route path="/subroutines/:id/extract" element={<LiveExtractionPage />} />
            <Route path="/subroutines/:id/spec" element={<DraftSpecPage />} />
            <Route path="/subroutines/:id/review" element={<SpecReviewPage />} />
            <Route path="/specs/:id/audit" element={<AuditTrailPage />} />
            <Route path="/specs/:id/scaffold" element={<LiveScaffoldPage />} />
            <Route path="/scaffolds/:id" element={<ScaffoldArtifactPage />} />
            <Route path="/scaffolds/:id/validation" element={<ValidationReportPage />} />
            <Route path="/my-reviews" element={<MyReviewsPage />} />
            <Route path="/comments" element={<CommentsPage />} />
            <Route path="/compliance" element={<CompliancePage />} />
            <Route path="/platform" element={<PlatformIndexPage />} />
            <Route path="/platform/prompts" element={<PromptCatalogPage />} />
            <Route path="/platform/golden-dataset" element={<GoldenDatasetPage />} />
            <Route path="/platform/harmonisation" element={<HarmonisationPage />} />
            <Route path="/platform/languages" element={<LanguagesPage />} />
            <Route path="/platform/roles" element={<RolesPage />} />
            <Route path="/platform/validation" element={<ValidationPolicyPage />} />
            <Route path="/platform/signatures" element={<SignatureHealthPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </main>
      </div>
      <KeyboardOverlay open={helpOpen} onClose={closeHelp} />
    </div>
  );
}
