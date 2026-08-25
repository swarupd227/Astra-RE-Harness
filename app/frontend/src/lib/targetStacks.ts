import type { ArchetypeManifest } from '@/lib/api';

/**
 * Target-stack eligibility — one source of truth for "can this routine
 * actually be scaffolded into this stack?".
 *
 * Previously TargetSelector grouped archetypes by stack and picked the first
 * production one as the headline WITHOUT consulting `compatibleSchemas`. A
 * Delphi routine therefore saw "Java Spring · Production" (because a UniBasic
 * archetype happens to be production) and the click failed server-side with
 * `scaffold.no_compatible_archetype`. The rules below mirror the server gate
 * in ScaffoldEndpoints.cs so the UI can only offer what the API will accept.
 */

export function isProduction(a: ArchetypeManifest): boolean {
  return (a.status ?? '').toLowerCase().startsWith('production');
}

/**
 * Schema compatibility, matching the server: an archetype declaring no
 * `compatibleSchemas` is treated as universal, and an unknown source
 * language (older rows have a null `sourceLanguage`) matches everything.
 */
export function matchesSchema(a: ArchetypeManifest, schema: string | null | undefined): boolean {
  if (!schema) return true;
  if (!a.compatibleSchemas || a.compatibleSchemas.length === 0) return true;
  return a.compatibleSchemas.some((s) => s.toLowerCase() === schema.toLowerCase());
}

export type StackOption = {
  stack: string;
  /** Card headline — the archetype that would actually be used, when there is one. */
  headline: ArchetypeManifest;
  variants: ArchetypeManifest[];
  /** Production AND schema-compatible: what the server would let through. */
  usable: ArchetypeManifest[];
  selectable: boolean;
  /** Why it can't be picked. Null when selectable. */
  blockedReason: string | null;
};

/**
 * Group archetypes into per-stack cards for a given source language.
 * Selectable stacks sort first so the viable choices lead.
 */
export function buildStackOptions(
  archetypes: ArchetypeManifest[],
  schema: string | null | undefined,
): StackOption[] {
  const byStack = new Map<string, ArchetypeManifest[]>();
  for (const a of archetypes) {
    const arr = byStack.get(a.targetStack) ?? [];
    arr.push(a);
    byStack.set(a.targetStack, arr);
  }

  const options: StackOption[] = [...byStack.entries()].map(([stack, variants]) => {
    const usable = variants.filter((a) => isProduction(a) && matchesSchema(a, schema));
    const compatible = variants.filter((a) => matchesSchema(a, schema));
    // Headline: what the server would pick, else the closest thing we have to
    // show — a compatible-but-preview archetype explains the gate better than
    // an unrelated production one.
    const headline = usable[0] ?? compatible[0] ?? variants[0];
    const lang = schema ? prettySchema(schema) : 'this source language';
    const blockedReason = usable.length > 0
      ? null
      : compatible.length > 0
        ? `${prettyStatus(headline.status)} — ships with a Nous pair engagement`
        : `No ${lang} archetype for this stack yet`;
    return { stack, headline, variants, usable, selectable: usable.length > 0, blockedReason };
  });

  return options.sort((a, b) => {
    if (a.selectable !== b.selectable) return a.selectable ? -1 : 1;
    return a.stack.localeCompare(b.stack);
  });
}

/**
 * The stack the platform would choose on its own for this source language.
 * Mirrors the server's default in ScaffoldEndpoints.cs: prefer dotnet8 when
 * it is viable, else the first viable stack alphabetically.
 */
export function recommendedStack(
  archetypes: ArchetypeManifest[],
  schema: string | null | undefined,
): string | null {
  const viable = buildStackOptions(archetypes, schema).filter((o) => o.selectable);
  if (viable.length === 0) return null;
  return viable.find((o) => o.stack === 'dotnet8')?.stack ?? viable[0].stack;
}

export function prettyStack(s: string): string {
  switch (s) {
    case 'dotnet8':     return '.NET 8';
    case 'dotnet10':    return '.NET 10';
    case 'java-spring': return 'Java Spring';
    default:            return s;
  }
}

export function prettySchema(s: string): string {
  switch (s) {
    case 'fortran-f77': return 'Fortran F77';
    case 'cobol':       return 'COBOL';
    case 'delphi':      return 'Delphi';
    case 'cpp':         return 'C++';
    case 'vb6':         return 'VB6';
    case 'unibasic':    return 'UniBasic';
    case 'openedge':    return 'OpenEdge ABL';
    case 'csharp':      return 'C#';
    case 'java':        return 'Java';
    case 'php':         return 'PHP';
    default:            return s;
  }
}

/** Compact display — strip everything after the first " · ". */
export function prettyStatus(s: string): string {
  const idx = (s ?? '').indexOf(' · ');
  return idx > 0 ? s.slice(0, idx) : (s ?? '');
}

/**
 * Convert a `canonical-<sourceLang>-<paradigm>` archetype id into a short
 * paradigm token for the variant list.
 */
export function paradigmFromId(archetypeId: string): string {
  const tail = archetypeId.split('-').pop() ?? archetypeId;
  switch (tail.toLowerCase()) {
    case 'winforms': return 'WinForms';
    case 'blazor':   return 'Blazor';
    case 'minapi':   return 'Min API';
    case 'fmt':      return 'fmt';
    case 'idsmtp':   return 'Indy SMTP';
    case 'spring':   return 'Spring';
    case 'net8':     return 'net8';
    default:         return tail;
  }
}
