/**
 * Reads the spec Claude is still writing. A real provider streams the spec as JSON text and the structured patches
 * only land when it has finished, so without this the page shows either nothing or raw JSON for a minute. This parses
 * the text written so far and keeps only what is complete: a string that has closed, an object that has closed inside
 * a list (one finished claim), and the finished keys of an object still being written. Nothing half-written is shown.
 *
 * Pure and tolerant: anything it cannot read yields what it read before that point, never an exception.
 */
export function parsePartialJson(text: string): unknown {
  const start = text.indexOf('{');
  if (start < 0) return undefined;
  const p = new Reader(text, start);
  try {
    const v = p.value();
    if (v === INCOMPLETE) return undefined;
    return isPartial(v) ? unwrap(v) : v;
  } catch {
    return undefined;
  }
}

const CLAIM_KEYS = ['summary', 'inputs', 'outputs', 'invariants', 'side_effects', 'edge_cases', 'open_questions'];
const WRAPPER_KEYS = ['additionalProperties', 'spec'];

function looksLikeSpec(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === 'object' && !Array.isArray(v) && CLAIM_KEYS.some((k) => k in (v as object));
}

/**
 * The spec being written, also when the model nests it — under `additionalProperties`, `spec` or a sole key, as an
 * object or as that object written out as a JSON string (the same shapes the API's SpecShape.Unwrap lifts at the end).
 * A spec still being written as a string is decoded as far as it goes and read like any other partial JSON.
 */
export function parsePartialSpec(text: string): Record<string, unknown> | undefined {
  const top = parsePartialJson(text);
  if (looksLikeSpec(top)) return top;
  const start = text.indexOf('{');
  if (start < 0) return undefined;
  const m = /^\{\s*"([^"\\]+)"\s*:\s*/.exec(text.slice(start));
  if (!m) return undefined;
  const key = m[1];
  const obj = top && typeof top === 'object' ? (top as Record<string, unknown>) : {};
  if (!WRAPPER_KEYS.includes(key) && Object.keys(obj).length > 1) return undefined;
  const nested = obj[key];
  if (looksLikeSpec(nested)) return nested;
  const rest = text.slice(start + m[0].length);
  if (rest.startsWith('"')) {
    const inner = parsePartialJson(decodeJsonStringPrefix(rest.slice(1)));
    return looksLikeSpec(inner) ? inner : undefined;
  }
  return undefined;
}

/** The text of a JSON string literal written so far (without its opening quote), escapes decoded; stops at its end. */
function decodeJsonStringPrefix(s: string): string {
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '"') break;
    if (c !== '\\') {
      out += c;
      continue;
    }
    const n = s[i + 1];
    if (n === undefined) break; // an escape cut off by the stream
    if (n === 'u') {
      const hex = s.slice(i + 2, i + 6);
      if (hex.length < 4) break;
      out += String.fromCharCode(parseInt(hex, 16));
      i += 5;
      continue;
    }
    out += ({ n: '\n', r: '\r', t: '\t', b: '\b', f: '\f' } as Record<string, string>)[n] ?? n;
    i += 1;
  }
  return out;
}

const INCOMPLETE = Symbol('incomplete');
type Value = unknown | typeof INCOMPLETE;

class Reader {
  private readonly s: string;
  private i: number;
  constructor(s: string, i: number) {
    this.s = s;
    this.i = i;
  }

  private ws() {
    while (this.i < this.s.length && /\s/.test(this.s[this.i])) this.i++;
  }

  private atEnd() {
    this.ws();
    return this.i >= this.s.length;
  }

  value(): Value {
    if (this.atEnd()) return INCOMPLETE;
    const c = this.s[this.i];
    if (c === '{') return this.object();
    if (c === '[') return this.array();
    if (c === '"') return this.string();
    return this.scalar();
  }

  /** An object being written keeps its finished keys; the caller decides whether a partial object counts. */
  private object(): Value {
    this.i++; // {
    const out: Record<string, unknown> = {};
    for (;;) {
      if (this.atEnd()) return partial(out);
      if (this.s[this.i] === '}') {
        this.i++;
        return out;
      }
      if (this.s[this.i] === ',') {
        this.i++;
        continue;
      }
      const key = this.string();
      if (key === INCOMPLETE || typeof key !== 'string') return partial(out);
      if (this.atEnd()) return partial(out);
      if (this.s[this.i] !== ':') throw new Error('expected :');
      this.i++;
      const v = this.value();
      if (v === INCOMPLETE) return partial(out);
      if (isPartial(v)) {
        // A list still being written keeps its finished items; an object still being written is dropped from a list
        // but kept as a top-level section value.
        out[key] = unwrap(v);
        return partial(out);
      }
      out[key] = v;
    }
  }

  /** A list being written keeps only its finished items. */
  private array(): Value {
    this.i++; // [
    const out: unknown[] = [];
    for (;;) {
      if (this.atEnd()) return partial(out);
      if (this.s[this.i] === ']') {
        this.i++;
        return out;
      }
      if (this.s[this.i] === ',') {
        this.i++;
        continue;
      }
      const v = this.value();
      if (v === INCOMPLETE || isPartial(v)) return partial(out);
      out.push(v);
    }
  }

  private string(): Value {
    if (this.s[this.i] !== '"') throw new Error('expected string');
    let j = this.i + 1;
    while (j < this.s.length) {
      const c = this.s[j];
      if (c === '\\') {
        j += 2;
        continue;
      }
      if (c === '"') {
        const raw = this.s.slice(this.i, j + 1);
        this.i = j + 1;
        return JSON.parse(raw) as string;
      }
      j++;
    }
    this.i = this.s.length;
    return INCOMPLETE;
  }

  private scalar(): Value {
    const m = /^(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null)/.exec(this.s.slice(this.i, this.i + 64));
    if (!m) throw new Error('unexpected token');
    const end = this.i + m[0].length;
    // A number at the very end of the text may still be growing.
    if (end >= this.s.length) {
      this.i = this.s.length;
      return INCOMPLETE;
    }
    this.i = end;
    return JSON.parse(m[0]);
  }
}

type Partial = { __partial: unknown };
function partial(v: unknown): Partial {
  return { __partial: v };
}
function isPartial(v: unknown): v is Partial {
  return typeof v === 'object' && v !== null && '__partial' in v;
}
function unwrap(v: Partial): unknown {
  return v.__partial;
}
