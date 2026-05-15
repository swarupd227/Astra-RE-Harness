import Editor, { type Monaco } from '@monaco-editor/react';
import { useCallback, useEffect, useRef } from 'react';
import type { editor } from 'monaco-editor';
import { tokens } from '@/tokens/tokens';

const FORTRAN_KEYWORDS = [
  'SUBROUTINE',
  'FUNCTION',
  'PROGRAM',
  'END',
  'RETURN',
  'IF',
  'THEN',
  'ELSE',
  'ENDIF',
  'DO',
  'WHILE',
  'GOTO',
  'CALL',
  'INCLUDE',
  'IMPLICIT',
  'NONE',
  'PARAMETER',
  'COMMON',
  'DIMENSION',
  'INTEGER',
  'REAL',
  'DOUBLE',
  'PRECISION',
  'COMPLEX',
  'CHARACTER',
  'LOGICAL',
  'DATA',
  'SAVE',
  'EQUIVALENCE',
  'EXTERNAL',
  'INTRINSIC',
  'OPEN',
  'CLOSE',
  'READ',
  'WRITE',
  'PRINT',
  'REWIND',
  'BACKSPACE',
  'STOP',
  'CONTINUE',
];

function registerFortran(monaco: Monaco) {
  if (monaco.languages.getLanguages().some((l) => l.id === 'fortran-fixed')) return;

  monaco.languages.register({ id: 'fortran-fixed' });
  monaco.languages.setMonarchTokensProvider('fortran-fixed', {
    ignoreCase: true,
    keywords: FORTRAN_KEYWORDS,
    tokenizer: {
      root: [
        // Fixed-form comment: any non-blank in column 1 (C, c, *, !)
        [/^[Cc*!].*$/, 'comment'],
        // Continuation marker in column 6
        [/^ {5}[^ \t]/, 'metatag'],
        // Inline ! comment
        [/!.*$/, 'comment'],
        // Strings
        [/'([^'\\]|\\.)*'/, 'string'],
        [/"([^"\\]|\\.)*"/, 'string'],
        // Numbers
        [/\b\d+\.\d*([eE][+-]?\d+)?\b/, 'number.float'],
        [/\b\d+\b/, 'number'],
        // Identifiers (keyword vs name)
        [/[A-Za-z_][A-Za-z0-9_]*/, {
          cases: {
            '@keywords': 'keyword',
            '@default': 'identifier',
          },
        }],
        // Operators
        [/\.[A-Za-z]+\./, 'operator'],
        [/[=+\-*/<>:]/, 'operator'],
      ],
    },
  });

  monaco.editor.defineTheme('astra-light', {
    base: 'vs',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '7A8497', fontStyle: 'italic' },
      { token: 'metatag', foreground: 'B9520B' },
      { token: 'keyword', foreground: '1F4FA8', fontStyle: 'bold' },
      { token: 'number', foreground: '0E7C66' },
      { token: 'number.float', foreground: '0E7C66' },
      { token: 'string', foreground: 'A8201A' },
      { token: 'operator', foreground: '475063' },
      { token: 'identifier', foreground: '101728' },
    ],
    colors: {
      'editor.background': '#FFFFFF',
      'editor.foreground': tokens.ink.primary,
      'editor.lineHighlightBackground': '#F1F1ED',
      'editorLineNumber.foreground': '#7A8497',
      'editorLineNumber.activeForeground': '#101728',
      'editor.selectionBackground': '#FBE7D6',
      'editorIndentGuide.background': '#E4E6EB',
    },
  });
}

export type Citation = { lineStart: number; lineEnd: number; tone?: 'accent' };

/** Lines containing TODO/NotImplementedException stubs — highlighted in scaffold view. */
export type TodoMarker = { lineStart: number; lineEnd: number; tooltip?: string };

export function MonacoSource({
  value,
  height = 520,
  language = 'fortran-fixed',
  citations = [],
  todoMarkers = [],
  highlightLine,
  className,
}: {
  value: string;
  height?: number | string;
  language?: 'fortran-fixed' | 'csharp' | 'json' | 'plaintext';
  citations?: Citation[];
  todoMarkers?: TodoMarker[];
  highlightLine?: number;
  className?: string;
}) {
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null);
  const monacoRef = useRef<Monaco | null>(null);
  const decorationsRef = useRef<editor.IEditorDecorationsCollection | null>(null);
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const spotlightTimerRef = useRef<number | null>(null);

  const applyDecorations = useCallback(() => {
    const monaco = monacoRef.current;
    if (!monaco || !decorationsRef.current) return;
    const decos: editor.IModelDeltaDecoration[] = [];
    for (const c of citations) {
      const isActive = c.tone === 'accent';
      decos.push({
        range: new monaco.Range(c.lineStart, 1, c.lineEnd, 1),
        options: {
          isWholeLine: true,
          // Three coordinated surfaces; all three replay their animations
          // when the citation becomes active. CSS handles
          // prefers-reduced-motion by collapsing the animation duration.
          className: isActive
            ? 'astra-cite-bg astra-cite-pulse'
            : 'astra-cite-bg',
          linesDecorationsClassName: isActive
            ? 'astra-cite-gutter astra-cite-gutter-pulse'
            : 'astra-cite-gutter',
          marginClassName: isActive
            ? 'astra-cite-margin astra-cite-margin-pulse'
            : 'astra-cite-margin',
        },
      });
    }
    for (const t of todoMarkers) {
      decos.push({
        range: new monaco.Range(t.lineStart, 1, t.lineEnd, 1),
        options: {
          isWholeLine: true,
          className: 'astra-todo-bg',
          linesDecorationsClassName: 'astra-todo-gutter',
          glyphMarginClassName: 'astra-todo-glyph',
          hoverMessage: t.tooltip ? { value: t.tooltip } : undefined,
        },
      });
    }
    decorationsRef.current.set(decos);
  }, [citations, todoMarkers]);

  const onMount = useCallback(
    (ed: editor.IStandaloneCodeEditor, monaco: Monaco) => {
      editorRef.current = ed;
      monacoRef.current = monaco;
      registerFortran(monaco);
      monaco.editor.setTheme('astra-light');
      decorationsRef.current = ed.createDecorationsCollection([]);
      applyDecorations();
      if (highlightLine) ed.revealLineInCenter(highlightLine);
    },
    [], // eslint-disable-line react-hooks/exhaustive-deps
  );

  // Reveal whenever highlightLine changes (e.g., on each citation_pulse).
  // After Monaco's smooth-scroll settles (~150 ms), briefly flash the
  // editor frame so the eye snaps to where the citation just landed.
  useEffect(() => {
    if (!highlightLine || !editorRef.current) return;
    editorRef.current.revealLineInCenter(highlightLine);
    const wrap = wrapperRef.current;
    if (!wrap) return;
    // Re-trigger the spotlight by removing then re-adding the class.
    wrap.classList.remove('astra-cite-spotlight');
    // void-read forces a reflow so the animation restarts.
    void wrap.offsetWidth;
    wrap.classList.add('astra-cite-spotlight');
    if (spotlightTimerRef.current) window.clearTimeout(spotlightTimerRef.current);
    spotlightTimerRef.current = window.setTimeout(() => {
      wrap.classList.remove('astra-cite-spotlight');
      spotlightTimerRef.current = null;
    }, 900);
  }, [highlightLine]);

  useEffect(() => () => {
    if (spotlightTimerRef.current) window.clearTimeout(spotlightTimerRef.current);
  }, []);

  // Re-apply decorations when citations / TODO markers change.
  useEffect(() => {
    applyDecorations();
  }, [applyDecorations]);

  // When `height === "100%"` the wrapper must have a definite height itself —
  // Monaco's internal container resolves against its parent, and a block-level
  // div with no height collapses to 0 and renders blank. Giving the wrapper
  // `h-full min-h-0` lets the consuming page drop us into a `flex-1 min-h-0`
  // slot and have Monaco fill it fluidly.
  const isFlexible = height === '100%';
  const wrapperClass = isFlexible ? `h-full min-h-0 ${className ?? ''}` : (className ?? '');
  const wrapperStyle = !isFlexible && typeof height === 'number' ? { height } : undefined;

  return (
    <div ref={wrapperRef} className={wrapperClass} style={wrapperStyle}>
      <Editor
        height={isFlexible ? '100%' : height}
        language={language}
        value={value}
        onMount={onMount}
        options={{
          readOnly: true,
          fontFamily: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace',
          fontSize: 13,
          lineHeight: 20,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          renderWhitespace: 'none',
          renderLineHighlight: 'all',
          smoothScrolling: true,
          cursorSmoothCaretAnimation: 'on',
          glyphMargin: todoMarkers.length > 0,
          folding: true,
          wordWrap: 'off',
        }}
      />
    </div>
  );
}
