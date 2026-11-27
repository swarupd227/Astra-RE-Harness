/**
 * What kind of code unit something is, in the words its own language uses: a PHP page or method, a VBA macro, a
 * COBOL paragraph, a Fortran subroutine. The platform calls every one of them a "code unit" (older material says
 * "routine" / "subroutine"); this short kind is shown beside the name so nobody has to guess.
 *
 * Pure and cheap: derived from the language, the parsed signature and the name only.
 */
const OFFICE_DOC = /\.(xlsm|xlsb|xls|xltm|docm|doc|dotm|pptm|ppt|accdb|mdb)\//i;

export function unitKind(language?: string | null, signature?: string | null, name?: string | null, path?: string | null): string {
  const lang = (language ?? '').toLowerCase();
  const sig = (signature ?? '').trim();
  const n = (name ?? '').trim();
  const lower = sig.toLowerCase();

  switch (lang) {
    case 'php': {
      if (/^php script\b/i.test(sig)) return /\.tpl(\.php)?$/i.test(n) || /\.tpl(\.php)?\b/i.test(sig) ? 'template' : 'page';
      if (/\bclass\b/.test(lower) && !/\bfunction\b/.test(lower)) return 'class';
      if (/\b(public|private|protected|static|abstract|final)\b[^(]*\bfunction\b/.test(lower)) return 'method';
      if (/\bfunction\b/.test(lower)) return 'function';
      if (/\.class$/i.test(n)) return 'class';
      return 'unit';
    }
    case 'csharp':
    case 'java':
    case 'vbnet': {
      const parts = n.split('.');
      if (parts.length >= 2 && parts[parts.length - 1] === parts[parts.length - 2]) return 'constructor';
      if (/\b(class|record|struct|interface)\b/.test(lower) && !lower.includes('(')) return 'class';
      if (lang === 'vbnet' && /\bsub new\b/.test(lower)) return 'constructor';
      return 'method';
    }
    case 'vb6':
    case 'vba': {
      const office = lang === 'vba' || OFFICE_DOC.test(path ?? '');
      if (/\b(function)\b/.test(lower)) return 'function';
      if (/\bproperty\b/.test(lower)) return 'property';
      if (/\bsub\b/.test(lower)) return office ? 'macro' : 'procedure';
      return office ? 'macro' : 'procedure';
    }
    case 'fortran-f77':
    case 'fortran': {
      if (/\bprogram\b/.test(lower)) return 'program';
      if (/\bfunction\b/.test(lower)) return 'function';
      if (/\bblock data\b/.test(lower)) return 'block data';
      return 'subroutine';
    }
    case 'cobol':
      return /\bprogram-id\b|\bprogram\b/.test(lower) ? 'program' : 'paragraph';
    case 'delphi': {
      if (/\bconstructor\b/.test(lower)) return 'constructor';
      if (/\bdestructor\b/.test(lower)) return 'destructor';
      if (/\bfunction\b/.test(lower)) return 'function';
      return 'procedure';
    }
    case 'cpp':
    case 'c': {
      const m = /([A-Za-z_]\w*)::~?([A-Za-z_]\w*)\s*\(/.exec(sig) ?? /([A-Za-z_]\w*)::~?([A-Za-z_]\w*)$/.exec(n);
      if (m) return m[1] === m[2] ? 'constructor' : 'method';
      return 'function';
    }
    case 'unibasic':
      return /\bfunction\b/.test(lower) ? 'function' : 'subroutine';
    case 'openedge':
      return /\bfunction\b/.test(lower) ? 'function' : 'procedure';
    default:
      if (/\bfunction\b/.test(lower)) return 'function';
      if (/\bsub(routine)?\b/.test(lower)) return 'subroutine';
      if (/\bprocedure\b/.test(lower)) return 'procedure';
      return 'unit';
  }
}
