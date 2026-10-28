import hljs from 'highlight.js/lib/core';
import type { LanguageFn } from 'highlight.js';

/**
 * Syntax colouring for the evidence viewer. It is bundled, not fetched: the viewer must work on
 * a laptop with no network, which the Monaco editor (loaded from a CDN) does not. Each language is
 * its own lazy chunk, so only the ones a page actually shows are downloaded.
 */
const LOADERS: Record<string, () => Promise<{ default: LanguageFn }>> = {
  sql: () => import('highlight.js/lib/languages/sql'),
  xml: () => import('highlight.js/lib/languages/xml'),
  json: () => import('highlight.js/lib/languages/json'),
  csharp: () => import('highlight.js/lib/languages/csharp'),
  java: () => import('highlight.js/lib/languages/java'),
  cpp: () => import('highlight.js/lib/languages/cpp'),
  fortran: () => import('highlight.js/lib/languages/fortran'),
  delphi: () => import('highlight.js/lib/languages/delphi'),
  vbnet: () => import('highlight.js/lib/languages/vbnet'),
  php: () => import('highlight.js/lib/languages/php'),
  ini: () => import('highlight.js/lib/languages/ini'),
  yaml: () => import('highlight.js/lib/languages/yaml'),
  markdown: () => import('highlight.js/lib/languages/markdown'),
};

// COBOL, UniBasic and OpenEdge have no highlight.js grammar: they show as plain, numbered text.
const BY_EXTENSION: Record<string, string> = {
  '.sql': 'sql', '.ddl': 'sql',
  '.xml': 'xml', '.dtsx': 'xml', '.config': 'xml', '.csproj': 'xml',
  '.json': 'json',
  '.cs': 'csharp', '.csx': 'csharp',
  '.java': 'java',
  '.c': 'cpp', '.cc': 'cpp', '.cpp': 'cpp', '.cxx': 'cpp', '.h': 'cpp', '.hh': 'cpp', '.hpp': 'cpp',
  '.f': 'fortran', '.for': 'fortran', '.f77': 'fortran', '.f90': 'fortran', '.f95': 'fortran', '.f03': 'fortran',
  '.pas': 'delphi', '.dpr': 'delphi', '.dfm': 'delphi',
  '.vb': 'vbnet', '.bas': 'vbnet', '.cls': 'vbnet', '.frm': 'vbnet',
  '.php': 'php',
  '.ini': 'ini', '.cfg': 'ini',
  '.yaml': 'yaml', '.yml': 'yaml',
  '.md': 'markdown',
};

export function languageFor(path: string): string | null {
  const dot = path.lastIndexOf('.');
  if (dot < 0) return null;
  return BY_EXTENSION[path.slice(dot).toLowerCase()] ?? null;
}

const loading = new Map<string, Promise<boolean>>();

async function ensureLanguage(name: string): Promise<boolean> {
  if (hljs.getLanguage(name)) return true;
  let p = loading.get(name);
  if (!p) {
    const load = LOADERS[name];
    p = load
      ? load().then((m) => {
          hljs.registerLanguage(name, m.default);
          return true;
        }).catch(() => false)
      : Promise.resolve(false);
    loading.set(name, p);
  }
  return p;
}

/**
 * Splits highlighted HTML at its newlines, closing the spans that are open at the end of a line and
 * reopening them on the next, so a block comment that runs over ten lines is coloured on all ten.
 * The input is highlight.js output: text is already escaped and tags are only <span class="…">.
 */
export function splitHighlightedLines(html: string): string[] {
  const lines: string[] = [];
  const open: string[] = [];
  let current = '';
  for (const m of html.matchAll(/(<\/span>)|(<span[^>]*>)|(\n)|([^<\n]+|<)/g)) {
    if (m[1]) {
      open.pop();
      current += m[1];
    } else if (m[2]) {
      open.push(m[2]);
      current += m[2];
    } else if (m[3]) {
      lines.push(current + '</span>'.repeat(open.length));
      current = open.join('');
    } else {
      current += m[4];
    }
  }
  lines.push(current + '</span>'.repeat(open.length));
  return lines;
}

/** One HTML string per line of `text`, coloured for the file's language; null when it has no grammar. */
export async function highlightLines(text: string, path: string): Promise<string[] | null> {
  const lang = languageFor(path);
  if (!lang || !(await ensureLanguage(lang))) return null;
  try {
    return splitHighlightedLines(hljs.highlight(text, { language: lang, ignoreIllegals: true }).value);
  } catch {
    return null;
  }
}
