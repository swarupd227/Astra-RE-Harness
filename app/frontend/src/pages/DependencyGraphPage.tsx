import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import ReactFlow, {
  Background,
  Controls,
  Handle,
  MarkerType,
  MiniMap,
  Position,
  type Edge as RfEdge,
  type Node as RfNode,
  type NodeProps,
} from 'reactflow';
import 'reactflow/dist/style.css';
import { api, type DependencyGraphResponse, type DependencyGraphNode } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Card, CardBody } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';

/**
 * Phase 8.0.a — Interactive dependency graph for a corpus.
 *
 * Reads the parser-extracted call graph + COMMON / copybook refs and
 * renders them as a left-to-right layered diagram. Each node colours by
 * the routine's most-progressed state (PARSED grey → SIGNED green →
 * COMMITTED dark green). Click a node to drill into the subroutine.
 *
 * Layout: simple topological-depth-from-roots. Roots (in-degree 0) sit
 * at column 0; their immediate callees at column 1; and so on. Within
 * a column we sort alphabetically for determinism. This avoids pulling
 * in a force-directed layout dependency (elkjs / dagre) for the first
 * cut; we can swap in a real auto-layout when the graph density
 * actually demands it.
 */
export function DependencyGraphPage() {
  const params = useParams<{ id: string }>();
  const corpusId = params.id!;
  const navigate = useNavigate();
  const [hideExternal, setHideExternal] = useState(false);
  const [hideSharedStorage, setHideSharedStorage] = useState(true); // start clean

  const graph = useQuery({
    queryKey: ['dependency-graph', corpusId],
    queryFn: () => api.getDependencyGraph(corpusId),
    staleTime: 5 * 60_000,
  });

  const corpus = useQuery({
    queryKey: ['corpus', corpusId],
    queryFn: () => api.getCorpus(corpusId),
  });

  const { rfNodes, rfEdges } = useMemo(
    () => layout(graph.data, { hideSharedStorage }),
    [graph.data, hideSharedStorage],
  );

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

  return (
    <div className="mx-auto max-w-[1400px] space-y-4 p-6 lg:p-10" data-testid="dependency-graph-page">
      <header className="space-y-1">
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase 8.0 · dependency-aware migration · {corpus.data?.name ?? corpusId}
        </p>
        <h1 className="text-display font-semibold text-ink-primary">Dependency graph</h1>
        <p className="max-w-3xl text-body-lg text-ink-secondary">
          Parser-extracted call graph + shared-storage references for the corpus's latest source
          version. Each node is a subroutine; arrows go from CALLER to CALLEE. This is the
          substrate the Phase 8.0.b wave planner reads to assign routines to migration waves.
        </p>
      </header>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_280px]">
        <Card className="overflow-hidden">
          <CardBody className="p-0">
            <div style={{ height: 640 }} className="border-border-subtle">
              <ReactFlow
                nodes={rfNodes}
                edges={rfEdges}
                fitView
                minZoom={0.2}
                maxZoom={1.5}
                nodeTypes={NODE_TYPES}
                onNodeClick={(_e, n) => {
                  if (n.data?.subroutineId) navigate(`/subroutines/${n.data.subroutineId}`);
                }}
                proOptions={{ hideAttribution: true }}
              >
                <Background gap={20} />
                <Controls showInteractive={false} />
                <MiniMap pannable zoomable />
              </ReactFlow>
            </div>
          </CardBody>
        </Card>

        <aside className="space-y-3">
          <Card>
            <CardBody>
              <p className="mb-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                Stats
              </p>
              <ul className="space-y-1 font-mono text-body-sm text-ink-secondary">
                <li>nodes: {g.stats.nodeCount}</li>
                <li>call edges: {g.stats.callEdgeCount}</li>
                <li>shared-storage edges: {g.stats.sharedStorageEdgeCount}</li>
                <li>SCCs (cycles): {g.stats.sccCount}</li>
                <li>leaves: {g.stats.leafCount}</li>
                <li>roots: {g.stats.rootCount}</li>
                <li>external callees: {g.stats.externalCalleeCount}</li>
              </ul>
            </CardBody>
          </Card>

          <Card>
            <CardBody className="space-y-2">
              <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                Filters
              </p>
              <label className="flex items-center gap-2 text-body-sm">
                <input
                  type="checkbox"
                  checked={hideSharedStorage}
                  onChange={(e) => setHideSharedStorage(e.target.checked)}
                />
                Hide shared-storage edges
              </label>
              <label className="flex items-center gap-2 text-body-sm text-ink-tertiary opacity-60">
                <input type="checkbox" checked={hideExternal} onChange={(e) => setHideExternal(e.target.checked)} disabled />
                Hide external callees (n/a — they don't render as nodes)
              </label>
            </CardBody>
          </Card>

          <Card>
            <CardBody>
              <p className="mb-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                Legend
              </p>
              <ul className="space-y-1 text-body-sm">
                <li><Badge tone="neutral">PARSED</Badge> default state</li>
                <li><Badge tone="review">DRAFT</Badge> extracted, awaiting review</li>
                <li><Badge tone="success">SIGNED</Badge> spec authoritative</li>
                <li><Badge tone="scaffolded">SCAFFOLDED</Badge> target code generated</li>
                <li><Badge tone="success">COMMITTED</Badge> on its way to prod</li>
              </ul>
            </CardBody>
          </Card>

          {g.externalCallees.length > 0 && (
            <Card>
              <CardBody>
                <p className="mb-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                  External callees ({g.externalCallees.length})
                </p>
                <p className="mb-2 text-body-sm text-ink-secondary">
                  Names called by in-corpus routines but not resolved to any subroutine here.
                  Typically system / OS / library calls or missing source.
                </p>
                <ul className="space-y-0.5 font-mono text-caption text-ink-secondary">
                  {g.externalCallees.slice(0, 16).map((n) => <li key={n}>{n}</li>)}
                  {g.externalCallees.length > 16 && <li className="opacity-60">…and {g.externalCallees.length - 16} more</li>}
                </ul>
              </CardBody>
            </Card>
          )}

          <Card>
            <CardBody>
              <Link to={`/corpora/${corpusId}`} className="text-body-sm text-accent hover:underline">
                ← Back to corpus
              </Link>
            </CardBody>
          </Card>
        </aside>
      </div>
    </div>
  );
}

// ───────────────────────────────────────────────────────────────────
// Layout — simple topological depth-from-roots
// ───────────────────────────────────────────────────────────────────

const COL_WIDTH = 220;
const ROW_HEIGHT = 70;

function layout(
  data: DependencyGraphResponse | undefined,
  opts: { hideSharedStorage: boolean },
): { rfNodes: RfNode[]; rfEdges: RfEdge[] } {
  if (!data) return { rfNodes: [], rfEdges: [] };

  // 1. Compute depth from any root (BFS). Nodes unreachable from a root
  //    (cycles disconnected from sources) get depth = max + 1 so they
  //    still render.
  const depth = new Map<string, number>();
  const adjOut = new Map<string, string[]>();
  for (const n of data.nodes) adjOut.set(n.id, []);
  for (const e of data.edges) {
    if (e.type !== 'call') continue;
    adjOut.get(e.from)?.push(e.to);
  }
  const queue: string[] = [];
  for (const n of data.nodes) {
    if (n.isRoot) {
      depth.set(n.id, 0);
      queue.push(n.id);
    }
  }
  while (queue.length > 0) {
    const u = queue.shift()!;
    const d = depth.get(u) ?? 0;
    for (const v of adjOut.get(u) ?? []) {
      if (!depth.has(v) || (depth.get(v) ?? 0) < d + 1) {
        depth.set(v, d + 1);
        queue.push(v);
      }
    }
  }
  let maxDepth = 0;
  for (const d of depth.values()) if (d > maxDepth) maxDepth = d;
  for (const n of data.nodes) {
    if (!depth.has(n.id)) depth.set(n.id, maxDepth + 1);
  }

  // 2. Group by depth, sort alphabetically within depth.
  const byDepth = new Map<number, DependencyGraphNode[]>();
  for (const n of data.nodes) {
    const d = depth.get(n.id)!;
    if (!byDepth.has(d)) byDepth.set(d, []);
    byDepth.get(d)!.push(n);
  }
  for (const list of byDepth.values()) list.sort((a, b) => a.name.localeCompare(b.name));

  // 3. Assign positions.
  const rfNodes: RfNode[] = [];
  for (const [d, list] of byDepth) {
    list.forEach((n, i) => {
      rfNodes.push({
        id: n.id,
        position: { x: d * COL_WIDTH, y: i * ROW_HEIGHT },
        data: {
          label: n.name,
          state: n.state,
          subroutineId: n.id,
          calleeCount: n.calleeCount,
          callerCount: n.callerCount,
          isRoot: n.isRoot,
          isLeaf: n.isLeaf,
        },
        type: 'routine',
      });
    });
  }

  // 4. Edges.
  const rfEdges: RfEdge[] = [];
  for (const e of data.edges) {
    if (opts.hideSharedStorage && e.type === 'shared-storage') continue;
    rfEdges.push({
      id: `${e.from}-${e.to}-${e.type}`,
      source: e.from,
      target: e.to,
      type: 'smoothstep',
      animated: false,
      style: e.type === 'shared-storage'
        ? { stroke: '#94a3b8', strokeDasharray: '4 4' }
        : { stroke: '#475569' },
      markerEnd: { type: MarkerType.ArrowClosed, color: e.type === 'shared-storage' ? '#94a3b8' : '#475569' },
      data: { type: e.type, viaBlock: e.viaBlock },
    });
  }

  return { rfNodes, rfEdges };
}

// ───────────────────────────────────────────────────────────────────
// Custom node component
// ───────────────────────────────────────────────────────────────────

function RoutineNode({ data }: NodeProps) {
  const state = data.state as string;
  const tone =
    state === 'COMMITTED' ? 'success' :
    state === 'SIGNED' ? 'success' :
    state === 'SCAFFOLDED' ? 'scaffolded' :
    state === 'DRAFT' || state === 'IN_REVIEW' ? 'review' :
    'neutral';
  return (
    <div className="min-w-[180px] rounded border border-border-subtle bg-raised p-2 shadow-sm" data-testid={`graph-node-${data.subroutineId}`}>
      <Handle type="target" position={Position.Left} style={{ background: '#475569' }} />
      <div className="flex items-center gap-1">
        <Badge tone={tone as 'success' | 'review' | 'neutral' | 'scaffolded'}>{state}</Badge>
        {data.isRoot && <span className="font-mono text-[10px] uppercase text-ink-tertiary">root</span>}
        {data.isLeaf && <span className="font-mono text-[10px] uppercase text-ink-tertiary">leaf</span>}
      </div>
      <p className="mt-1 truncate font-mono text-body-sm font-semibold text-ink-primary">{data.label}</p>
      <p className="mt-0.5 font-mono text-[10px] text-ink-tertiary">
        callees: {data.calleeCount} · callers: {data.callerCount}
      </p>
      <Handle type="source" position={Position.Right} style={{ background: '#475569' }} />
    </div>
  );
}

const NODE_TYPES = { routine: RoutineNode };
