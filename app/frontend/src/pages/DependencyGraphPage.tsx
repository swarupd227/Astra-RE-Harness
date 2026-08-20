import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import cytoscape from 'cytoscape';
// @ts-expect-error — no bundled types for cytoscape-fcose
import fcose from 'cytoscape-fcose';
import { ZoomIn, ZoomOut, Maximize2, X } from 'lucide-react';
import { api } from '@/lib/api';
import { CardBody, Card } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { StateBadge } from '@/components/StateBadge';
import { EmptyState } from '@/components/EmptyState';
import { AwaitingDataIllustration } from '@/illustrations/AwaitingData';

cytoscape.use(fcose);

/**
 * Phase 8.0.a — Interactive dependency graph for a corpus.
 *
 * Powered by Cytoscape with the fcose (Fast Compound Spring Embedder)
 * layout — the same engine ACE uses for its ontology graph. Click any
 * routine to highlight its full upstream + downstream neighbourhood
 * (orange edges, focused-node halo, everything else fades). Double-click
 * to drill into the routine.
 */
export function DependencyGraphPage() {
  const params = useParams<{ id: string }>();
  const corpusId = params.id!;
  const navigate = useNavigate();
  const containerRef = useRef<HTMLDivElement | null>(null);
  const cyRef = useRef<cytoscape.Core | null>(null);
  const lastTapRef = useRef<{ id: string; t: number } | null>(null);

  const [hideSharedStorage, setHideSharedStorage] = useState(true);
  const [selected, setSelected] = useState<string | null>(null);

  const graph = useQuery({
    queryKey: ['dependency-graph', corpusId],
    queryFn: () => api.getDependencyGraph(corpusId),
    staleTime: 5 * 60_000,
  });

  const corpus = useQuery({
    queryKey: ['corpus', corpusId],
    queryFn: () => api.getCorpus(corpusId),
  });

  const plan = useQuery({
    queryKey: ['migration-plan', corpusId],
    queryFn: () => api.getCurrentMigrationPlan(corpusId).catch(() => null),
    retry: false,
  });

  const waveByRoutine = useMemo(() => {
    const m = new Map<string, number>();
    if (!plan.data) return m;
    for (const w of plan.data.waves) {
      for (const r of w.routines) m.set(r.id, w.waveNumber);
    }
    return m;
  }, [plan.data]);

  const nodeById = useMemo(() => {
    const m = new Map<string, { name: string; state: string; calleeCount: number; callerCount: number }>();
    if (!graph.data) return m;
    for (const n of graph.data.nodes) m.set(n.id, n);
    return m;
  }, [graph.data]);

  // Build Cytoscape elements + style + layout. Runs once per (graph,
  // wave-plan, hideSharedStorage) change. We tear down the previous
  // Cytoscape instance on cleanup so React doesn't double-mount.
  useEffect(() => {
    if (!graph.data || !containerRef.current) return;
    const visibleEdges = graph.data.edges.filter(
      (e) => !(hideSharedStorage && e.type === 'shared-storage'),
    );
    const elements: cytoscape.ElementDefinition[] = [
      ...graph.data.nodes.map((n) => {
        const wave = waveByRoutine.get(n.id);
        const accent = wave !== undefined ? WAVE_FILL[Math.min(5, wave) as 1 | 2 | 3 | 4 | 5] : '#94a3b8';
        return {
          data: {
            id: n.id,
            label: n.name,
            wave: wave ?? null,
            state: n.state,
            accent,
            statedot: STATE_DOT[n.state] ?? '#94a3b8',
          },
        };
      }),
      ...visibleEdges.map((e, i) => ({
        data: {
          id: `e${i}`,
          source: e.from,
          target: e.to,
          rel: e.type === 'shared-storage' ? 'COMMON' : 'call',
          shared: e.type === 'shared-storage' ? 1 : 0,
        },
      })),
    ];

    const cy = cytoscape({
      container: containerRef.current,
      elements,
      style: [
        {
          selector: 'node',
          style: {
            'background-color': 'data(accent)',
            'background-opacity': 0.18,
            'border-color': 'data(accent)',
            'border-width': 2,
            'border-opacity': 0.9,
            label: 'data(label)',
            'font-size': 9,
            color: '#0f172a',
            'font-weight': 600,
            'text-valign': 'bottom',
            'text-halign': 'center',
            'text-margin-y': 4,
            width: 32,
            height: 32,
            shape: 'ellipse',
          } as cytoscape.Css.Node,
        },
        // Coloured ring around each node keyed to wave (already on
        // border-color above). Add a small inner glyph showing the
        // wave number when assigned.
        {
          selector: 'node[wave]',
          style: {
            'font-size': 13,
            color: 'data(accent)',
            'font-weight': 800,
            // Replace the routine name in the centre with the wave
            // number — the routine name still shows below the node.
            'text-margin-y': 4,
          } as cytoscape.Css.Node,
        },
        {
          selector: 'edge',
          style: {
            width: 1.4,
            'line-color': '#cbd5e1',
            'curve-style': 'bezier',
            'target-arrow-shape': 'triangle',
            'target-arrow-color': '#cbd5e1',
            'arrow-scale': 0.9,
          } as cytoscape.Css.Edge,
        },
        {
          selector: 'edge[shared = 1]',
          style: {
            'line-style': 'dashed',
            'line-dash-pattern': [4, 4],
          } as cytoscape.Css.Edge,
        },
        // Dimmed-out class for everything outside the focused subtree.
        {
          selector: '.faded',
          style: { opacity: 0.12 } as cytoscape.Css.Node,
        },
        // Highlighted-edge class — bright brand orange.
        {
          selector: 'edge.hl',
          style: {
            'line-color': '#f26722',
            'target-arrow-color': '#f26722',
            width: 2.6,
            opacity: 1,
          } as cytoscape.Css.Edge,
        },
        // Highlighted-node class — kept fully opaque so the focused
        // subtree pops against the dimmed background.
        {
          selector: 'node.hl',
          style: {
            'border-width': 3,
            'border-color': '#f26722',
            opacity: 1,
          } as cytoscape.Css.Node,
        },
        // Selected node — bigger, brand-bordered, slight shadow.
        {
          selector: 'node:selected',
          style: {
            'border-width': 4,
            'border-color': '#f26722',
            'background-color': '#f26722',
            'background-opacity': 0.22,
            width: 42,
            height: 42,
          } as cytoscape.Css.Node,
        },
      ],
      layout: {
        name: 'fcose',
        animate: true,
        animationDuration: 600,
        randomize: true,
        // Small corpora (few edges, many disconnected nodes) pack into a
        // tight grid where the long routine labels overlap — spread them
        // much wider. Disconnected (degree-0) nodes are placed by fcose's
        // tiling pass, so the tiling padding is what actually separates
        // them, not repulsion. Large graphs keep the denser defaults.
        idealEdgeLength: graph.data.nodes.length <= 30 ? 190 : 95,
        nodeRepulsion: graph.data.nodes.length <= 30 ? 50000 : 7000,
        padding: 24,
        nodeSeparation: graph.data.nodes.length <= 30 ? 240 : 80,
        tilingPaddingVertical: graph.data.nodes.length <= 30 ? 90 : 10,
        tilingPaddingHorizontal: graph.data.nodes.length <= 30 ? 130 : 10,
      } as cytoscape.LayoutOptions,
      wheelSensitivity: 0.25,
      minZoom: 0.25,
      maxZoom: 3,
    });
    cyRef.current = cy;

    // Single-tap on node: focus its neighbourhood.
    // Double-tap on the same node: drill into the routine page.
    cy.on('tap', 'node', (evt) => {
      const id = evt.target.id();
      const now = Date.now();
      const last = lastTapRef.current;
      if (last && last.id === id && now - last.t < 340) {
        lastTapRef.current = null;
        navigate(`/subroutines/${id}`);
        return;
      }
      lastTapRef.current = { id, t: now };
      setSelected(id);
      // Compute the WHOLE neighbourhood (any-hop predecessors +
      // any-hop successors). Cytoscape's `closedNeighborhood` only
      // walks 1 hop — we need the full transitive closure to match
      // the "blast radius" semantics in the sidebar.
      cy.elements().addClass('faded').removeClass('hl');
      const node = evt.target as cytoscape.NodeSingular;
      const upstream = node.predecessors();
      const downstream = node.successors();
      const subtree = node.union(upstream).union(downstream);
      subtree.removeClass('faded').addClass('hl');
    });

    // Background tap = clear.
    cy.on('tap', (evt) => {
      if (evt.target === cy) {
        setSelected(null);
        cy.elements().removeClass('faded hl');
      }
    });

    return () => {
      cy.destroy();
      cyRef.current = null;
    };
  }, [graph.data, waveByRoutine, hideSharedStorage, navigate]);

  const focused = selected ? nodeById.get(selected) : null;

  if (graph.isPending || corpus.isPending) {
    return (
      <div className="mx-auto max-w-[1400px] space-y-3 p-6">
        <Skeleton className="h-8 w-72" />
        <Skeleton className="h-[600px]" />
      </div>
    );
  }
  if (graph.isError || !graph.data) {
    return (
      <div className="mx-auto max-w-[1400px] p-6">
        <ErrorBlock title="Could not load dependency graph" message={String(graph.error)} />
      </div>
    );
  }

  const g = graph.data;

  if (g.nodes.length === 0) {
    return (
      <div className="mx-auto max-w-[1400px] space-y-4 p-6 lg:p-10 fadeup">
        <PageHero
          tone="violet"
          eyebrow={`Phase 8.0 · dependency-aware migration · ${corpus.data?.name ?? corpusId}`}
          title="Dependency graph"
          lead="Parser-extracted call graph + shared-storage references."
        />
        <Card>
          <CardBody>
            <EmptyState
              illustration={<AwaitingDataIllustration size={140} />}
              title="No call graph yet"
              description="This corpus has no cross-routine CALL or shared-storage references to graph — either it hasn't finished parsing, or every routine is self-contained."
            />
          </CardBody>
        </Card>
      </div>
    );
  }

  return (
    <div
      className="mx-auto max-w-[1400px] space-y-4 p-6 lg:p-10 fadeup"
      data-testid="dependency-graph-page"
    >
      <PageHero
        tone="violet"
        eyebrow={`Phase 8.0 · dependency-aware migration · ${corpus.data?.name ?? corpusId}`}
        title="Dependency graph"
        lead="Parser-extracted call graph + shared-storage references. Nodes colour by their assigned migration wave. Click any routine to highlight its full upstream and downstream path · double-click to drill in."
      />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_280px]">
        <Card className="overflow-hidden">
          <CardBody className="p-0">
            <div className="relative" style={{ height: 640, background: '#f8fafc' }}>
              <div ref={containerRef} className="absolute inset-0" data-testid="dep-graph-canvas" />
              {/* Floating control cluster — top-right. */}
              <div className="absolute right-3 top-3 flex flex-col gap-1 rounded-lg border border-border-subtle bg-white/95 p-1 shadow-card">
                <button
                  type="button"
                  onClick={() => cyRef.current?.zoom(cyRef.current.zoom() * 1.25)}
                  className="flex h-7 w-7 items-center justify-center rounded-md text-ink-secondary hover:bg-sunken"
                  title="Zoom in"
                >
                  <ZoomIn size={14} />
                </button>
                <button
                  type="button"
                  onClick={() => cyRef.current?.zoom(cyRef.current.zoom() * 0.8)}
                  className="flex h-7 w-7 items-center justify-center rounded-md text-ink-secondary hover:bg-sunken"
                  title="Zoom out"
                >
                  <ZoomOut size={14} />
                </button>
                <button
                  type="button"
                  onClick={() => cyRef.current?.fit(undefined, 32)}
                  className="flex h-7 w-7 items-center justify-center rounded-md text-ink-secondary hover:bg-sunken"
                  title="Fit to view"
                >
                  <Maximize2 size={14} />
                </button>
              </div>
              {/* Floating wave + state legend — bottom-left. */}
              <div className="pointer-events-none absolute bottom-3 left-3 rounded-lg border border-border-subtle bg-white/95 p-2.5 shadow-card">
                <p className="label mb-1.5">Wave</p>
                <div className="space-y-1">
                  {[1, 2, 3, 4, 5].map((w) => (
                    <div key={w} className="flex items-center gap-2">
                      <span
                        className="inline-block h-2.5 w-2.5 rounded-full"
                        style={{ background: WAVE_FILL[w as 1 | 2 | 3 | 4 | 5] }}
                      />
                      <span className="font-mono text-[10px] text-ink-secondary">{w}</span>
                    </div>
                  ))}
                </div>
              </div>
              {selected && (
                <button
                  type="button"
                  onClick={() => {
                    setSelected(null);
                    cyRef.current?.elements().removeClass('faded hl');
                  }}
                  className="pointer-events-auto absolute bottom-3 right-3 inline-flex items-center gap-1.5 rounded-full bg-white/95 px-2.5 py-1 text-[11px] font-semibold text-ink-secondary shadow-card hover:text-ink-primary"
                >
                  <X size={12} /> Clear focus
                </button>
              )}
            </div>
            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border-subtle bg-sunken px-3 py-2 font-mono text-[11px] text-ink-tertiary">
              <span>
                {focused
                  ? <>Focused on <span className="font-semibold text-ink-primary">{focused.name}</span> · click background to clear · double-click to drill in</>
                  : <>Drag to pan · scroll to zoom · click a routine to focus its path · double-click to drill in</>
                }
              </span>
            </div>
          </CardBody>
        </Card>

        <aside className="space-y-3">
          <div className="card p-4">
            <p className="label mb-2">Stats</p>
            <ul className="space-y-1 font-mono text-[12px] text-ink-secondary">
              <li>nodes: {g.stats.nodeCount}</li>
              <li>call edges: {g.stats.callEdgeCount}</li>
              <li>shared-storage edges: {g.stats.sharedStorageEdgeCount}</li>
              <li>SCCs (cycles): {g.stats.sccCount}</li>
              <li>leaves: {g.stats.leafCount}</li>
              <li>roots: {g.stats.rootCount}</li>
              <li>external callees: {g.stats.externalCalleeCount}</li>
              <li>modules: {g.moduleGraph.stats.moduleCount}</li>
              <li>module cycles: {g.moduleGraph.stats.cyclicGroupCount}</li>
            </ul>
          </div>

          {g.moduleGraph.cycles.length > 0 && (
            <div className="card p-4" data-testid="module-cycles">
              <p className="label mb-2">Module cycles</p>
              <ul className="space-y-2 font-mono text-[11px] text-ink-secondary">
                {g.moduleGraph.cycles.map((c) => (
                  <li key={c.id}>
                    <span className="font-semibold text-ink-primary">{c.id}</span>
                    <span className="text-ink-tertiary"> · {c.members.length} modules</span>
                    <ul className="mt-1 space-y-0.5 pl-3">
                      {c.members.map((m) => (
                        <li key={m} className="truncate" title={m}>{m}</li>
                      ))}
                    </ul>
                  </li>
                ))}
              </ul>
              <p className="mt-2 text-[11px] text-ink-tertiary">
                Modules in a cycle call each other both ways — they must be modernized together as one slice.
              </p>
            </div>
          )}

          <div className="card p-4">
            <p className="label mb-2">Filters</p>
            <label className="flex items-center gap-2 text-[12px] text-ink-secondary">
              <input
                type="checkbox"
                checked={hideSharedStorage}
                onChange={(e) => setHideSharedStorage(e.target.checked)}
                className="rounded border-border accent-brand-500"
              />
              Hide shared-storage edges
            </label>
          </div>

          {focused && (
            <div className="card p-4">
              <p className="label mb-2">Focused routine</p>
              <p className="font-mono text-[13px] font-semibold text-ink-primary">{focused.name}</p>
              <div className="mt-2 flex items-center gap-2">
                <StateBadge state={focused.state} />
                {selected && waveByRoutine.has(selected) && (
                  <span className="pill bg-ace-50 text-ace-700 ring-1 ring-ace-100">
                    wave {waveByRoutine.get(selected)}
                  </span>
                )}
              </div>
              <ul className="mt-2 space-y-1 font-mono text-[11px] text-ink-tertiary">
                <li>callees: {focused.calleeCount}</li>
                <li>callers: {focused.callerCount}</li>
              </ul>
              <Link
                to={`/subroutines/${selected}`}
                className="mt-2 inline-block text-[12px] font-semibold text-brand-600 hover:text-brand-700 hover:underline"
              >
                Open routine →
              </Link>
            </div>
          )}

          {g.externalCallees.length > 0 && (
            <div className="card p-4">
              <p className="label mb-2">External callees ({g.externalCallees.length})</p>
              <p className="mb-2 text-[12px] text-ink-secondary">
                Names called by in-corpus routines but not resolved here. Typically system / OS / library calls.
              </p>
              <ul className="space-y-0.5 font-mono text-[11px] text-ink-secondary">
                {g.externalCallees.slice(0, 16).map((n) => <li key={n}>{n}</li>)}
                {g.externalCallees.length > 16 && (
                  <li className="opacity-60">…and {g.externalCallees.length - 16} more</li>
                )}
              </ul>
            </div>
          )}

          <div className="card p-4">
            <Link
              to={`/corpora/${corpusId}`}
              className="text-[12px] font-semibold text-brand-600 hover:text-brand-700 hover:underline"
            >
              ← Back to corpus
            </Link>
          </div>
        </aside>
      </div>
    </div>
  );
}

// ── Wave-coloured palette (matches Tailwind wave-* tokens). ────────
const WAVE_FILL: Record<1 | 2 | 3 | 4 | 5, string> = {
  1: '#059669', // emerald
  2: '#0d9488', // teal
  3: '#4f46e5', // indigo
  4: '#7c3aed', // violet
  5: '#d97706', // amber
};

// Migration-state colours for the corner dot — light-theme adjusted.
const STATE_DOT: Record<string, string> = {
  PARSED:     '#94a3b8',
  DRAFT:      '#d97706',
  IN_REVIEW:  '#d97706',
  SIGNED:     '#4f46e5',
  SCAFFOLDED: '#7c3aed',
  COMMITTED:  '#059669',
};
