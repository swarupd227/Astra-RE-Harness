/**
 * Force-directed graph layout. Pure JavaScript — no external deps.
 *
 * Adapted from the CUDE knowledge-graph reference: a Hooke / Coulomb
 * spring-electric model where connected nodes attract via Hooke's law,
 * all nodes repel pairwise (Coulomb's law), and a gentle gravity well
 * keeps the cluster centred. Cooling schedule prevents oscillation.
 *
 * Returns positions augmented onto the input nodes — nothing else; the
 * caller renders via SVG with the (x, y) coordinates returned.
 */

export interface LayoutNode {
  id: string;
  [key: string]: unknown;
}

export interface LayoutEdge {
  source: string;
  target: string;
  [key: string]: unknown;
}

export interface PositionedNode<N extends LayoutNode = LayoutNode> {
  x: number;
  y: number;
  vx: number;
  vy: number;
  node: N;
}

export function forceDirectedLayout<N extends LayoutNode>(
  nodes: N[],
  edges: LayoutEdge[],
  options: {
    width?: number;
    height?: number;
    iterations?: number;
    repulsionForce?: number;
    attractionForce?: number;
    damping?: number;
    idealEdgeLength?: number;
    centerGravity?: number;
  } = {},
): PositionedNode<N>[] {
  const {
    width = 1600,
    height = 1000,
    iterations = 140,
    repulsionForce = 18_000,
    attractionForce = 0.005,
    damping = 0.85,
    idealEdgeLength = 220,
    centerGravity = 0.01,
  } = options;

  if (!nodes.length) return [];

  const margin = 80;
  const positioned: PositionedNode<N>[] = nodes.map((node) => ({
    x: margin + Math.random() * (width - 2 * margin),
    y: margin + Math.random() * (height - 2 * margin),
    vx: 0,
    vy: 0,
    node,
  }));

  const indexById = new Map<string, number>();
  positioned.forEach((p, i) => indexById.set(p.node.id, i));

  const edgePairs = edges
    .map((e) => ({ source: indexById.get(e.source), target: indexById.get(e.target) }))
    .filter((e): e is { source: number; target: number } => e.source !== undefined && e.target !== undefined);

  for (let iter = 0; iter < iterations; iter++) {
    const temp = 1 - iter / iterations;
    const fx = new Float64Array(positioned.length);
    const fy = new Float64Array(positioned.length);

    // Coulomb repulsion: all-pairs.
    for (let i = 0; i < positioned.length; i++) {
      for (let j = i + 1; j < positioned.length; j++) {
        const dx = positioned[j].x - positioned[i].x;
        const dy = positioned[j].y - positioned[i].y;
        const distSq = dx * dx + dy * dy || 1;
        const dist = Math.sqrt(distSq);
        const force = repulsionForce / distSq;
        const fxc = (dx / dist) * force;
        const fyc = (dy / dist) * force;
        fx[i] -= fxc;
        fy[i] -= fyc;
        fx[j] += fxc;
        fy[j] += fyc;
      }
    }

    // Hooke attraction along edges.
    for (const { source, target } of edgePairs) {
      const dx = positioned[target].x - positioned[source].x;
      const dy = positioned[target].y - positioned[source].y;
      const dist = Math.sqrt(dx * dx + dy * dy) || 1;
      const force = (dist - idealEdgeLength) * attractionForce;
      const fxc = (dx / dist) * force;
      const fyc = (dy / dist) * force;
      fx[source] += fxc;
      fy[source] += fyc;
      fx[target] -= fxc;
      fy[target] -= fyc;
    }

    // Gravity well.
    const cx = width / 2;
    const cy = height / 2;
    for (let i = 0; i < positioned.length; i++) {
      fx[i] += (cx - positioned[i].x) * centerGravity;
      fy[i] += (cy - positioned[i].y) * centerGravity;
    }

    // Integrate with damping + cooling, clamp speed, keep in bounds.
    for (let i = 0; i < positioned.length; i++) {
      positioned[i].vx = (positioned[i].vx + fx[i]) * damping * temp;
      positioned[i].vy = (positioned[i].vy + fy[i]) * damping * temp;
      const speed = Math.hypot(positioned[i].vx, positioned[i].vy);
      const maxSpeed = 30 * temp;
      if (speed > maxSpeed) {
        positioned[i].vx = (positioned[i].vx / speed) * maxSpeed;
        positioned[i].vy = (positioned[i].vy / speed) * maxSpeed;
      }
      positioned[i].x += positioned[i].vx;
      positioned[i].y += positioned[i].vy;
      positioned[i].x = Math.max(margin, Math.min(width - margin, positioned[i].x));
      positioned[i].y = Math.max(margin, Math.min(height - margin, positioned[i].y));
    }
  }

  return positioned.map((p) => ({ ...p, x: Math.round(p.x), y: Math.round(p.y) }));
}

/**
 * BFS the up-to-`maxHops`-neighborhood of a selected node. Returns the
 * set of node ids and edge indices that are in scope so the caller can
 * dim everything outside.
 */
export function getNeighborhood<E extends LayoutEdge>(
  nodes: LayoutNode[],
  edges: E[],
  selectedId: string | null,
  maxHops = 99,
): { nodeIds: Set<string>; edgeIndices: Set<number> } {
  if (!selectedId) {
    return {
      nodeIds: new Set(nodes.map((n) => n.id)),
      edgeIndices: new Set(edges.map((_, i) => i)),
    };
  }

  const adj = new Map<string, { neighbor: string; edgeIdx: number }[]>();
  edges.forEach((e, i) => {
    if (!adj.has(e.source)) adj.set(e.source, []);
    if (!adj.has(e.target)) adj.set(e.target, []);
    adj.get(e.source)!.push({ neighbor: e.target, edgeIdx: i });
    adj.get(e.target)!.push({ neighbor: e.source, edgeIdx: i });
  });

  const visited = new Set<string>([selectedId]);
  const edgeIndices = new Set<number>();
  let frontier = [selectedId];

  for (let hop = 0; hop < maxHops && frontier.length > 0; hop++) {
    const next: string[] = [];
    for (const nodeId of frontier) {
      for (const { neighbor, edgeIdx } of adj.get(nodeId) ?? []) {
        edgeIndices.add(edgeIdx);
        if (!visited.has(neighbor)) {
          visited.add(neighbor);
          next.push(neighbor);
        }
      }
    }
    frontier = next;
  }

  return { nodeIds: visited, edgeIndices };
}
